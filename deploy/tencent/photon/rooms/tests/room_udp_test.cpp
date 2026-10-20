#include "battle_http.hpp"
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
    int actor_id = 0;

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
    void send(const ser::Message &message, bool reliable = true) {
        auto data = protocol.Serialize(message);
        require(bool(data), "Cannot serialize request");
        require(peer.send_payload(*data, {.channel = 0,
                                          .mode = reliable ? luxon::enet::EnetDeliveryMode::Reliable
                                                           : luxon::enet::EnetDeliveryMode::Unreliable}),
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
        auto joined = request(
            game, create ? 227 : 226,
            {{255, name}, {249, actor}, {248, properties}, {204, std::vector<std::string>{"GluonPlugin"}}},
            accepted);
        if (accepted)
            game.actor_id = int(dawnshard::integer(joined.parameters.at(254)));
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
    Json wait_room(int count, int viewer = 101) {
        Json room;
        until(
            [&] {
                long status;
                room = get("/Get/ByViewerId/" + std::to_string(viewer), status);
                return status == 200 && room["players"].size() == static_cast<size_t>(count);
            },
            "Room state did not converge");
        return room;
    }
    void stage(Client &client, int value, bool accepted = true) {
        auto props = std::make_shared<ser::Hashtable>(
            ser::Hashtable{{std::string("GoToIngameState"), int32_t(value)}});
        try {
            request(client, 252, {{251, props}, {254, int32_t(client.actor_id)}}, accepted);
        } catch (const std::exception &error) {
            throw std::runtime_error("Actor " + std::to_string(client.actor_id) + " stage " +
                                     std::to_string(value) + ": " + error.what());
        }
    }
    void raise(Client &client, uint8_t code, ser::ByteArray data, bool reliable = true) {
        client.send(
            ser::Message(ser::OperationRequestMessage{253, {{244, code}, {245, std::move(data)}}}, true),
            reliable);
    }
    size_t events(const Client &client, uint8_t code, bool server_only = true) const {
        return std::ranges::count_if(client.events, [&](const auto &event) {
            if (event.event_code != code)
                return false;
            auto sender = event.parameters.find(254);
            return !server_only ||
                   (sender != event.parameters.end() && dawnshard::integer(sender->second) == 0);
        });
    }
    ser::ByteArray event_data(const Client &client, uint8_t code) const {
        for (auto it = client.events.rbegin(); it != client.events.rend(); ++it)
            if (it->event_code == code && dawnshard::integer(it->parameters.at(254)) == 0)
                return it->parameters.at(245).get<ser::ByteArray>();
        throw std::runtime_error("Missing expected server event");
    }
    void pause(int milliseconds) {
        auto deadline = Clock::now() + std::chrono::milliseconds(milliseconds);
        while (Clock::now() < deadline)
            pump();
    }
    void load(Client &host, Client &guest) {
        host.events.clear();
        guest.events.clear();
        stage(guest, 1, false);
        stage(host, 1);
        stage(guest, 1);
        stage(host, 2);
        stage(guest, 2);
        stage(host, 3);
        stage(guest, 3);
        until([&] { return events(host, 0x3e) && events(guest, 0x3e); },
              "Party allocation was not delivered");
        stage(host, 4);
        pause(50);
        require(events(host, 0x14) == 0, "Character data sent before both clients reached stage 4");
        stage(guest, 4);
        until([&] { return events(host, 0x14) == 2 && events(guest, 0x14) == 2; },
              "Character data was not delivered");
        for (const auto &event : host.events) {
            if (event.event_code != 0x14)
                continue;
            auto data = dawnshard::decode_messagepack(event.parameters.at(245).get<ser::ByteArray>(), 65536);
            require(data.is_array() && data.size() == 5 && data[2].size() == 2 && data[4].size() == 4,
                    "Two-player character/AI allocation did not match the original wire format");
            require(data[2][0].size() == 161 && data[2][0][0] == 80 && data[2][0][93] == 1.25,
                    "Hero parameter wire fields were not preserved");
        }
        raise(host, 0x03, Json::to_msgpack(Json::array({1})));
        pause(60);
        require(events(host, 0x15) == 0 && events(guest, 0x15) == 0,
                "Battle started before guest finished loading");
        raise(guest, 0x03, Json::to_msgpack(Json::array({1})));
        until([&] { return events(host, 0x15) == 1 && events(guest, 0x15) == 1; },
              "Battle did not start after both Ready events");
        raise(guest, 0x03, Json::to_msgpack(Json::array({1})));
        pause(50);
        require(events(host, 0x15) == 1, "Duplicate Ready restarted battle");
    }
    int record_count(int viewer) {
        CURL *curl = curl_easy_init();
        std::string body;
        std::string url =
            std::string(std::getenv("LUXON_TEST_API_URL")) + "/records/" + std::to_string(viewer);
        curl_easy_setopt(curl, CURLOPT_URL, url.c_str());
        curl_easy_setopt(curl, CURLOPT_PROXY, "");
        curl_easy_setopt(curl, CURLOPT_TIMEOUT_MS, 2000L);
        curl_easy_setopt(curl, CURLOPT_WRITEDATA, &body);
        curl_easy_setopt(
            curl, CURLOPT_WRITEFUNCTION, +[](char *data, size_t size, size_t count, void *target) -> size_t {
                static_cast<std::string *>(target)->append(data, size * count);
                return size * count;
            });
        auto code = curl_easy_perform(curl);
        curl_easy_cleanup(curl);
        require(code == CURLE_OK, "Test game API unavailable");
        return std::stoi(body);
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
        scenario.request(guest, 252, {{251, start}, {254, int32_t(guest.actor_id)}}, false);
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

        if (!std::getenv("LUXON_TEST_API_URL"))
            return 0;
        auto &battle_host = scenario.join("luxon-battle-smoke", 201, true);
        auto &battle_guest = scenario.join("luxon-battle-smoke", 202, false);
        auto battle_room = scenario.wait_room(2, 201);
        int battle_id = battle_room["roomId"];
        scenario.load(battle_host, battle_guest);
        scenario.until(
            [&] {
                long code;
                scenario.get("/Get/ById/" + std::to_string(battle_id), code);
                return code == 404;
            },
            "In-progress room remained joinable through room list");
        scenario.join("luxon-battle-smoke", 203, false, false);
        for (bool reliable : {true, false}) {
            auto count = scenario.events(battle_guest, 0x01, false);
            scenario.raise(battle_host, 0x01, {1, 2, 3, 4}, reliable);
            scenario.until([&] { return scenario.events(battle_guest, 0x01, false) > count; },
                           "Battle action was not relayed");
        }
        auto start_count = scenario.events(battle_host, 0x15, false);
        scenario.raise(battle_guest, 0x15, {0x80});
        scenario.pause(50);
        require(scenario.events(battle_host, 0x15, false) == start_count,
                "Client forged a server StartQuest event");

        auto clear_payload = [](int viewer) {
            auto record = Json::to_msgpack(Json{{"dungeon_key", "smoke-" + std::to_string(viewer)},
                                                {"is_host", false},
                                                {"member_count", 99},
                                                {"connecting_viewer_id_list", Json::array({99999})}});
            return Json::to_msgpack(Json::array({1, Json::binary(record)}));
        };
        scenario.raise(battle_host, 0x3f, clear_payload(201));
        scenario.raise(battle_host, 0x3f, clear_payload(201));
        scenario.until([&] { return scenario.events(battle_host, 0x40) > 0; },
                       "Host quest result not delivered");
        require(scenario.events(battle_guest, 0x40) == 0, "Host quest result leaked to guest");
        auto host_result = dawnshard::decode_messagepack(scenario.event_data(battle_host, 0x40), 65536);
        auto host_record = Json::from_msgpack(host_result[1].get_binary());
        require(host_record["test_api_viewer"] == 201 && host_record["is_host"] == true &&
                    host_record["member_count"] == 2 &&
                    host_record["connecting_viewer_id_list"] == Json::array({202}),
                "Host clear request was not augmented from server state");
        auto returned = scenario.events(battle_host, 0x40);
        scenario.raise(battle_host, 0x3f, clear_payload(201));
        scenario.until([&] { return scenario.events(battle_host, 0x40) > returned; },
                       "Duplicate clear did not replay cached result");
        require(scenario.record_count(201) == 1, "Duplicate clear wrote rewards more than once");
        scenario.raise(battle_guest, 0x3f, clear_payload(202));
        scenario.until([&] { return scenario.events(battle_guest, 0x40) > 0; },
                       "Guest quest result not delivered");
        auto guest_result = dawnshard::decode_messagepack(scenario.event_data(battle_guest, 0x40), 65536);
        auto guest_record = Json::from_msgpack(guest_result[1].get_binary());
        require(guest_record["test_api_viewer"] == 202 && guest_record["is_host"] == false &&
                    guest_record["member_count"] == 2 &&
                    guest_record["connecting_viewer_id_list"] == Json::array({201}),
                "Guest clear used another actor's identity");
        require(scenario.record_count(202) == 1, "Guest result was submitted more than once");
        scenario.raise(battle_host, 0x18, {0x80});
        auto reopened = scenario.wait_room(2, 201);
        require(reopened["roomId"] != battle_id, "Room number was not renewed after clear");

        // Load a second battle and exercise the failure responses independently.
        scenario.load(battle_host, battle_guest);
        scenario.raise(battle_host, 0x43, Json::to_msgpack(Json::array({1, 0})));
        scenario.raise(battle_guest, 0x43, Json::to_msgpack(Json::array({1, 1})));
        scenario.until(
            [&] { return scenario.events(battle_host, 0x44) && scenario.events(battle_guest, 0x44); },
            "Quest failure not acknowledged");
        require(dawnshard::decode_messagepack(scenario.event_data(battle_host, 0x44), 1024)[1] == 0,
                "Timeup response mismatch");
        require(dawnshard::decode_messagepack(scenario.event_data(battle_guest, 0x44), 1024)[1] == 1,
                "AllDead response mismatch");
        scenario.leave(battle_guest);
        scenario.leave(battle_host);

        auto &error_host = scenario.join("luxon-http-failure", 991, true);
        auto &error_guest = scenario.join("luxon-http-failure", 999, false);
        scenario.wait_room(2, 991);
        scenario.stage(error_host, 1);
        scenario.stage(error_guest, 1);
        scenario.stage(error_host, 2);
        scenario.stage(error_guest, 2);
        scenario.until(
            [&] { return scenario.events(error_host, 0x17) && scenario.events(error_guest, 0x17); },
            "Preparation API failure did not close the room");
        require(!scenario.events(error_host, 0x15), "Battle started without role data");
        scenario.leave(error_guest);
        scenario.leave(error_host);
        auto &record_host = scenario.join("luxon-record-failure", 997, true);
        auto &record_guest = scenario.join("luxon-record-failure", 998, false);
        scenario.wait_room(2, 997);
        scenario.load(record_host, record_guest);
        scenario.raise(record_guest, 0x3f, clear_payload(998));
        scenario.until([&] { return scenario.events(record_guest, 0x17) > 0; },
                       "Record API failure did not report room failure");
        require(scenario.events(record_guest, 0x40) == 0,
                "Failed API call fabricated a successful quest result");
        scenario.raise(record_guest, 0x3f, clear_payload(998));
        scenario.pause(100);
        require(scenario.record_count(998) == 1, "Failed reward write was automatically retried");
        scenario.leave(record_guest);
        scenario.leave(record_host);

        auto &leaving_host = scenario.join("luxon-loading-leave", 901, true);
        auto &leaving_guest = scenario.join("luxon-loading-leave", 902, false);
        scenario.wait_room(2, 901);
        scenario.stage(leaving_host, 1);
        scenario.stage(leaving_guest, 1);
        scenario.leave(leaving_guest);
        scenario.until([&] { return scenario.events(leaving_host, 0x17) > 0; },
                       "Loading continued after roster changed");
        require(scenario.events(leaving_host, 0x15) == 0, "Loading disconnect incorrectly started battle");
        scenario.leave(leaving_host);
        std::cout << "Two-player loading, Ready barrier, reliable/unreliable actions, isolated results, "
                     "duplicate clear, room reuse and failure handling passed (synthetic game API)\n";
        return 0;
    } catch (const std::exception &error) {
        std::cerr << "Room UDP smoke failed: " << error.what() << '\n';
        return 1;
    }
}
