#include "battle_session.hpp"

#include <algorithm>
#include <luxon/common_codes.hpp>
#include <luxon/server/server_manager.hpp>

namespace dawnshard {
BattleSession::BattleSession(server::Game *game, std::shared_ptr<server::logger> log,
                             std::function<void()> hide, std::function<void()> reopen)
    : game_(game->weak_from_this()), log_(std::move(log)), hide_(std::move(hide)),
      reopen_(std::move(reopen)) {}

bool BattleSession::validate_stage(int actor, int stage) const {
    if (broken_ || stage < 0 || stage > 4)
        return false;
    if (!active_)
        return stage == 0 || (actor == 1 && stage == 1);
    auto member = members_.find(actor);
    if (member == members_.end())
        return false;
    if (stage == 0)
        return member->second.finished;
    return !member->second.finished && stage >= member->second.stage;
}

void BattleSession::on_stage(int actor, int stage) {
    if (!active_ && actor == 1 && stage == 1)
        begin();
    if (broken_ || !active_ || !members_.contains(actor))
        return;
    members_.at(actor).stage = stage;
    advance();
}

void BattleSession::begin() {
    auto game = game_.lock();
    if (!game)
        return;
    active_ = true;
    members_.clear();
    Json roster = Json::array();
    for (auto &peer : game->peers) {
        auto viewer = integer(property(peer.actor_props, "ViewerId"));
        auto slots = property(peer.actor_props, "UsePartySlot").get<std::vector<int32_t>>();
        Member member;
        member.actor = {{"actorNr", peer.actor_id},
                        {"viewerId", viewer},
                        {"partySlots", slots},
                        {"questId", integer(property(game->custom_props, "C0"))}};
        members_.emplace(peer.actor_id, std::move(member));
        roster.push_back(Json::array({peer.actor_id, viewer}));
    }
    if (!members_.contains(1) || members_.empty()) {
        break_room();
        return;
    }
    game->is_open = false;
    game->is_visible = false;
    hide_();
    game_properties({{std::string("GoToIngameInfo"), Json::to_msgpack(Json::array({roster, nullptr}))}});
    load_deadline_ = std::chrono::steady_clock::now() + std::chrono::seconds(120);
    log_->info("Co-op loading started: {} player(s)", members_.size());
    schedule_poll();
}

Json BattleSession::actors() const {
    Json result = Json::array();
    for (const auto &[id, member] : members_)
        result.push_back(member.actor);
    return result;
}

int BattleSession::minimum_stage() const {
    int result = 4;
    for (const auto &[id, member] : members_)
        result = std::min(result, member.stage);
    return result;
}

void BattleSession::advance() {
    if (broken_ || running_)
        return;
    auto game = game_.lock();
    if (!game)
        return;
    int stage = minimum_stage();
    if (stage >= 2 && !preparation_ && prepared_.is_null()) {
        bool solo = false;
        if (auto value = property(game->custom_props, "IsSoloPlayWithPhoton").get_ptr<bool>())
            solo = *value;
        preparation_ = BattleHttp::instance().post("Prepare", {{"actors", actors()}, {"isSolo", solo}});
        log_->info("Co-op role data requested");
        schedule_poll();
    }
    if (prepared_.is_null())
        return;
    if (stage >= 3 && !party_sent_) {
        emit(0x3e, base64_decode(prepared_.at("party").get<std::string>()));
        party_sent_ = true;
        log_->info("Co-op party allocation sent");
    }
    if (stage >= 4 && !characters_sent_) {
        for (const auto &data : prepared_.at("characters"))
            emit(0x14, base64_decode(data.get<std::string>()));
        characters_sent_ = true;
        log_->info("Co-op character data sent");
    }
}

void BattleSession::schedule_poll() {
    if (polling_)
        return;
    auto game = game_.lock();
    if (!game)
        return;
    polling_ = true;
    // Scheduled only on the UDP thread; worker threads never access the task
    // queue.
    game->get_server_manager().add_scheduled_task(20, [weak = weak_from_this()] {
        if (auto session = weak.lock()) {
            session->polling_ = false;
            session->poll();
        }
    });
}

void BattleSession::poll() {
    auto game = game_.lock();
    if (!game || !active_ || broken_)
        return;
    try {
        if (preparation_ && preparation_->wait_for(std::chrono::seconds(0)) == std::future_status::ready) {
            auto reply = preparation_->get();
            preparation_.reset();
            if (reply.status != 200 || !reply.body.is_object()) {
                log_->warn("Co-op role preparation failed; room closed");
                break_room();
                return;
            }
            prepared_ = std::move(reply.body);
            for (auto &[id, member] : members_) {
                member.count = prepared_.at("memberCounts").at(std::to_string(id)).get<int>();
                if (member.count < 1 || member.count > 4)
                    throw std::runtime_error("Invalid role allocation");
            }
            advance();
        }
        for (auto &[id, member] : members_) {
            if (!member.record ||
                member.record->wait_for(std::chrono::seconds(0)) != std::future_status::ready)
                continue;
            auto reply = member.record->get();
            member.record.reset();
            if (reply.status != 200 || !reply.body.is_object()) {
                log_->warn("Co-op quest record failed; no automatic retry");
                break_room();
                return;
            }
            auto response = base64_decode(reply.body.at("response").get<std::string>());
            if (response.empty())
                throw std::runtime_error("Empty quest result");
            member.response = Json::to_msgpack(Json::array({1, Json::binary(response), 0, false}));
            member.finished = true;
            emit(0x40, member.response, id);
            log_->info("Co-op quest result delivered");
        }
        maybe_reset();
        bool pending = preparation_.has_value();
        for (const auto &[id, member] : members_)
            pending |= member.record.has_value();
        if (active_ && !running_ && std::chrono::steady_clock::now() >= load_deadline_) {
            log_->warn("Co-op loading timed out");
            break_room(2);
        } else if (pending || (active_ && !running_))
            schedule_poll();
    } catch (const std::exception &) {
        log_->warn("Co-op response could not be processed");
        break_room();
    }
}

bool BattleSession::on_event(server::GamePeer &actor, server::Event &event) {
    if (broken_ || !active_ || !members_.contains(actor.actor_id))
        return false;
    auto &member = members_.at(actor.actor_id);
    try {
        switch (event.code) {
        case 0x03:
            if (!characters_sent_ || member.finished)
                return false;
            member.ready = true;
            if (!running_ &&
                std::ranges::all_of(members_, [](const auto &entry) { return entry.second.ready; })) {
                running_ = true;
                emit(0x15, Json::to_msgpack(Json::object()));
                log_->info("Co-op battle started: all players loaded");
            }
            return true;
        case 0x3f:
            if (!running_ || member.finished) {
                if (!member.response.empty())
                    emit(0x40, member.response, actor.actor_id);
                return false;
            }
            clear(actor, event.data.get<ser::ByteArray>());
            return false;
        case 0x43:
            if (!running_ || member.finished || member.record)
                return false;
            fail(actor, event.data.get<ser::ByteArray>());
            return false;
        case 0x48:
            member.dead = true;
            return true;
        case 0x18:
            if (actor.actor_id == 1 && running_ && member.finished) {
                reset_requested_ = true;
                maybe_reset();
            }
            return false;
        default:
            return true;
        }
    } catch (const std::exception &) {
        log_->warn("Invalid co-op control event ignored");
        return false;
    }
}

void BattleSession::clear(server::GamePeer &actor, const ser::ByteArray &payload) {
    auto &member = members_.at(actor.actor_id);
    if (member.record)
        return; // A duplicate must never issue a second reward write.
    Json data = decode_messagepack(payload, 768 * 1024);
    if (!data.is_array() || data.size() != 2 || !data[1].is_binary())
        throw std::invalid_argument("Invalid clear request");
    const auto &record = data[1].get_binary();
    if (record.empty() || record.size() > 524288)
        throw std::invalid_argument("Invalid record size");
    auto game = game_.lock();
    if (!game)
        return;
    int room_id = int(integer(property(game->custom_props, "RoomId")));
    if (room_id > 0)
        game_properties(
            {{std::string("GoToIngameInfo"), ser::null}, {std::string("RoomId"), int32_t(-room_id)}});
    Json request = {{"actors", actors()},   {"actorNr", actor.actor_id}, {"memberCount", member.count},
                    {"roomName", game->id}, {"roomId", room_id},         {"record", base64_encode(record)}};
    const auto &bet = property(actor.actor_props, "AstralBetCount");
    if (!bet.is_null())
        request["astralBetCount"] = integer(bet);
    member.record = BattleHttp::instance().post("Clear", std::move(request));
    log_->info("Co-op quest record requested");
    schedule_poll();
}

void BattleSession::fail(server::GamePeer &actor, const ser::ByteArray &payload) {
    Json data = decode_messagepack(payload, 1024);
    if (!data.is_array() || data.size() != 2 || !data[1].is_number_integer())
        throw std::invalid_argument("Invalid failure request");
    int type = data[1].get<int>();
    if (type != 0 && type != 1)
        throw std::invalid_argument("Invalid failure type");
    auto &member = members_.at(actor.actor_id);
    member.finished = true;
    member.ready = false;
    emit(0x44, Json::to_msgpack(Json::array({1, type == 0 ? 0 : 1})), actor.actor_id);
    auto game = game_.lock();
    if (game) {
        ser::Hashtable stage{{std::string("GoToIngameState"), int32_t(0)}};
        game->insert_actor_props(actor.actor_id, stage);
        int room = int(integer(property(game->custom_props, "RoomId")));
        game_properties(
            {{std::string("GoToIngameInfo"), ser::null}, {std::string("RoomId"), int32_t(-std::abs(room))}});
    }
    log_->info("Co-op quest failure acknowledged");
}

void BattleSession::maybe_reset() {
    if (!reset_requested_ ||
        !std::ranges::all_of(members_, [](const auto &entry) { return entry.second.finished; }))
        return;
    auto game = game_.lock();
    if (!game || !game->find_peer(1))
        return;
    active_ = false;
    running_ = party_sent_ = characters_sent_ = reset_requested_ = false;
    preparation_.reset();
    prepared_ = nullptr;
    members_.clear();
    for (auto &peer : game->peers)
        game->insert_actor_props(peer.actor_id, {{std::string("GoToIngameState"), int32_t(0)}});
    emit(0x18, Json::to_msgpack(Json::object()));
    game_properties({{std::string("GoToIngameInfo"), ser::null}});
    reopen_();
    log_->info("Co-op room returned to preparation");
}

void BattleSession::on_leave(int actor) {
    if (!active_ || broken_)
        return;
    if (actor == 1) {
        broken_ = true; // The room plugin sends the existing HostDisconnected event.
        return;
    }
    members_.erase(actor);
    if (!running_)
        break_room(); // Don't start with role data for a roster that changed
                      // mid-load.
    else
        maybe_reset();
}

void BattleSession::break_room(int reason) {
    if (broken_)
        return;
    broken_ = true;
    active_ = true;
    if (auto game = game_.lock()) {
        game->is_open = false;
        game->is_visible = false;
        hide_();
        emit(0x17, Json::to_msgpack(Json::array({1, reason})));
    }
}

void BattleSession::emit(uint8_t code, ser::ByteArray bytes, int actor) {
    if (auto game = game_.lock()) {
        server::Event event{.code = code, .sender_actor_id = 0};
        if (actor)
            event.receivers = std::unordered_set<int32_t>{actor};
        else
            event.receivers = uint8_t(luxon::ReceiverGroup::All);
        event.data = std::move(bytes);
        game->broadcast_event(event);
    }
}

void BattleSession::game_properties(ser::Hashtable properties) {
    if (auto game = game_.lock()) {
        game->insert_game_props(properties);
        auto event = game->create_property_update_event(0, std::move(properties), 0);
        event.receivers = uint8_t(luxon::ReceiverGroup::All);
        game->broadcast_event(event);
    }
}
} // namespace dawnshard
