#include "room_data.hpp"
#include <chrono>
#include <curl/curl.h>
#include <functional>
#include <iostream>
#include <luxon/enet_peer.hpp>
#include <luxon/ser_gp_binary_v16.hpp>
#include <luxon/server/server_manager.hpp>
#include <thread>

namespace ser = luxon::ser;
using Clock = std::chrono::steady_clock;
using dawnshard::Json;

void require(bool value, const std::string &message) {
    if (!value)
        throw std::runtime_error(message);
}

struct Client {
    luxon::enet::UdpSocket socket;
    luxon::enet::EnetPeer peer;
    ser::GpBinaryV16 protocol;
    std::vector<ser::OperationResponseMessage> responses;
    std::vector<ser::EventMessage> events;
    bool encrypted = false;
    bool initialized = false;

    Client(const std::string &host, uint16_t port) : peer(luxon::enet::EnetPeerConfig{}) {
        peer.on_payload_command = [this](const luxon::enet::EnetCommand &command) {
            // v1.5 can emit its default-1.8 AppStats before processing our 1.6 Init.
            const auto payload = command.get_payload();
            if (!initialized && payload.size() >= 2 && payload[1] != 1)
                return;
            auto message = protocol.Deserialize(command.get_payload());
            if (!message)
                throw std::runtime_error("Client could not decode response: " + message.error().message);
            if (message->get_if<ser::InitResponseMessage>())
                initialized = true;
            if (auto crypto = message->get_if<ser::InternalOperationResponseMessage>()) {
                require(bool(protocol.HandleInitEncryptionResponse(*crypto)), "Encryption failed");
                encrypted = true;
            }
            if (auto response = message->get_if<ser::OperationResponseMessage>())
                responses.push_back(*response);
            if (auto event = message->get_if<ser::EventMessage>())
                events.push_back(*event);
        };
        require(peer.connect(socket, host, port), "UDP connect failed");
    }
    void send(const ser::Message &message) {
        auto data = protocol.Serialize(message);
        require(bool(data), "Cannot serialize request");
        require(peer.send_payload(*data, {.channel = 0, .mode = luxon::enet::EnetDeliveryMode::Reliable}),
                "Peer was not connected");
    }
    void tick() {
        uint8_t buffer[65536];
        luxon::enet::EnetEndpoint from;
        while (auto count = socket.recv_from(buffer, sizeof(buffer), from))
            peer.handle_incoming_datagram(ser::ByteArray(buffer, buffer + count));
        peer.service();
        while (peer.dispatch_one()) {
        }
    }
};

class Scenario {
    std::unique_ptr<server::ServerManager> manager_;
    std::vector<std::unique_ptr<Client>> clients_;
    std::string host_ = "127.0.0.1";
    uint16_t master_port_ = 25055, game_port_ = 25056;

  public:
    Scenario() {
        if (const char *external = std::getenv("LUXON_TEST_EXTERNAL_SERVER")) {
            host_ = external;
            master_port_ = 5055;
            game_port_ = 5056;
            return;
        }
        server::ServerManagerConfig config;
        config.enable_ipv6 = false;
        config.no_banner = true;
        config.add_server(server::ServerType::MasterServer, 25055, "127.0.0.1:25055");
        config.add_server(server::ServerType::GameServer, 25056, "127.0.0.1:25056");
        manager_ = std::make_unique<server::ServerManager>(config);
    }
    ~Scenario() {
        // Drain callbacks before destroying the embedded server and its task queue.
        for (auto &client : clients_)
            client->peer.disconnect();
        for (int i = 0; i < 20; ++i)
            pump();
    }

    void pump() {
        if (manager_)
            manager_->run_once();
        for (auto &client : clients_)
            client->tick();
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }
    void until(std::function<bool()> predicate, const char *message, int seconds = 6) {
        auto deadline = Clock::now() + std::chrono::seconds(seconds);
        while (!predicate() && Clock::now() < deadline)
            pump();
        require(predicate(), message);
    }
    Client &connect(uint16_t port) {
        auto &client = *clients_.emplace_back(std::make_unique<Client>(host_, port));
        until([&] { return client.peer.state() == luxon::enet::EnetConnectionState::Connected; },
              "UDP connection timed out");
        ser::InitMessage init;
        init.protocol_minor = 6;
        init.app_id = "LoadBalancing";
        client.send(ser::Message(init));
        until([&] { return client.initialized; }, "UDP initialization timed out");
        auto crypto = client.protocol.CreateInitEncryptionRequest();
        require(bool(crypto), "Cannot initialize encryption");
        client.peer.send_payload(*crypto, {.channel = 0, .mode = luxon::enet::EnetDeliveryMode::Reliable});
        until([&] { return client.encrypted; }, "UDP encryption timed out");
        return client;
    }
    ser::OperationResponseMessage request(Client &client, uint8_t operation, ser::Dictionary parameters = {},
                                          std::optional<bool> ok = true) {
        client.responses.clear();
        client.send(ser::Message(ser::OperationRequestMessage{operation, std::move(parameters)}, true));
        until([&] { return !client.responses.empty(); }, "Operation timed out");
        auto response = client.responses.back();
        require(response.operation_code == operation, "Unexpected response opcode");
        if (ok == true)
            require(response.return_code == 0,
                    "Operation rejected: " +
                        response.debug_message.value_or(std::to_string(response.return_code)));
        if (ok == false)
            require(response.return_code != 0, "Invalid operation unexpectedly succeeded");
        return response;
    }
    void leave(Client &client) {
        client.send(ser::Message(ser::OperationRequestMessage{254, {}}, true));
        until([&] { return client.peer.state() == luxon::enet::EnetConnectionState::Disconnected; },
              "Leave did not disconnect the client");
    }
    Client &join(const std::string &name, int viewer, bool create, bool accepted = true) {
        auto &master = connect(master_port_);
        request(master, 230, {{224, std::string("room-test")}, {220, std::string("1")}});
        auto invitation = request(master, create ? 227 : 226, {{255, name}},
                                  accepted ? std::optional<bool>(true) : std::nullopt);
        if (invitation.return_code != 0)
            return master; // Capacity can be rejected by Master already.
        auto token = invitation.parameters[221];
        master.peer.disconnect();
        // Complete the handoff, as the phone does between Master and Game servers.
        auto until_time = Clock::now() + std::chrono::milliseconds(200);
        while (Clock::now() < until_time)
            pump();
        auto &game = connect(game_port_);
        request(game, 230, {{221, token}});
        auto actor = std::make_shared<ser::Hashtable>(
            ser::Hashtable{{std::string("PlayerId"), std::to_string(viewer)},
                           {std::string("UsePartySlot"), std::vector<int32_t>{1}}});
        auto properties = std::make_shared<ser::Hashtable>(
            ser::Hashtable{{std::string("C0"), int32_t(301010103)},
                           {std::string("MatchingCompatibleId"), int32_t(36)},
                           {std::string("MatchingType"), int32_t(1)}});
        request(
            game, create ? 227 : 226,
            {{255, name}, {249, actor}, {248, properties}, {204, std::vector<std::string>{"GluonPlugin"}}},
            accepted);
        return game;
    }
    Json get(const std::string &path, long &status) {
        CURL *curl = curl_easy_init();
        std::string body;
        std::string url = std::string(std::getenv("LUXON_STATE_URL")) + path;
        curl_easy_setopt(curl, CURLOPT_URL, url.c_str());
        curl_easy_setopt(curl, CURLOPT_PROXY, "");
        curl_easy_setopt(curl, CURLOPT_TIMEOUT_MS, 1000L);
        curl_easy_setopt(
            curl, CURLOPT_WRITEFUNCTION, +[](char *data, size_t size, size_t count, void *target) -> size_t {
                static_cast<std::string *>(target)->append(data, size * count);
                return size * count;
            });
        curl_easy_setopt(curl, CURLOPT_WRITEDATA, &body);
        auto code = curl_easy_perform(curl);
        curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &status);
        curl_easy_cleanup(curl);
        if (code != CURLE_OK || status != 200)
            return nullptr;
        return Json::parse(body);
    }
    Json wait_room(int count) {
        Json room;
        until(
            [&] {
                long status;
                room = get("/Get/ByViewerId/101", status);
                return status == 200 && room["players"].size() == static_cast<size_t>(count);
            },
            "Room state did not converge");
        return room;
    }
};

int main() {
    try {
        Scenario scenario;
        auto &host = scenario.join("luxon-udp-smoke", 101, true);
        auto initial = scenario.wait_room(1);
        int room_id = initial["roomId"];
        require(room_id >= 1000000 && room_id <= 9999999, "Seven digit room ID missing");
        auto &guest = scenario.join("luxon-udp-smoke", 102, false);
        scenario.wait_room(2);
        if (std::getenv("LUXON_TEST_LEASE")) {
            auto until_time = Clock::now() + std::chrono::seconds(65);
            while (Clock::now() < until_time)
                scenario.pump();
            auto renewed = scenario.wait_room(2);
            require(renewed["roomId"] == initial["roomId"] &&
                        renewed["startEntryTime"] == initial["startEntryTime"],
                    "Room lease was not renewed");
        }
        scenario.join("luxon-udp-smoke", 102, false, false); // duplicate viewer
        auto &remaining = scenario.join("luxon-udp-smoke", 103, false);
        scenario.join("luxon-udp-smoke", 104, false);
        scenario.wait_room(4);
        scenario.join("luxon-udp-smoke", 105, false, false); // fifth player

        // A guest cannot change room access; the host can. By-ID lookup still works.
        auto privacy =
            std::make_shared<ser::Hashtable>(ser::Hashtable{{std::string("MatchingType"), int32_t(2)}});
        scenario.request(guest, 252, {{251, privacy}}, false);
        scenario.request(host, 252, {{251, privacy}});
        scenario.until(
            [&] {
                long status;
                auto game = scenario.get("/Get/ById/" + std::to_string(room_id), status);
                return status == 200 && game["matchingType"] == 2;
            },
            "Private room was not searchable by ID");
        long status;
        auto public_rooms = scenario.get("/Get/GameList?questId=301010103", status);
        require(status == 200 && public_rooms.empty(), "Private room leaked into public list");

        auto start =
            std::make_shared<ser::Hashtable>(ser::Hashtable{{std::string("GoToIngameState"), int32_t(1)}});
        scenario.request(host, 252, {{251, start}, {254, int32_t(1)}}, false);
        scenario.leave(guest);
        scenario.wait_room(3);
        scenario.leave(host);
        scenario.until(
            [&] {
                long code;
                scenario.get("/Get/ById/" + std::to_string(room_id), code);
                return code == 404;
            },
            "Closed room remained searchable");
        scenario.until(
            [&] {
                for (const auto &event : remaining.events)
                    if (event.event_code == 0x17)
                        return true;
                return false;
            },
            "Remaining guest did not receive RoomBroken");
        std::cout
            << "UDP 1.6 encryption, Master/Game handoff, room state, privacy, capacity and close passed\n";
        return 0;
    } catch (const std::exception &error) {
        std::cerr << "Room UDP smoke failed: " << error.what() << '\n';
        return 1;
    }
}
