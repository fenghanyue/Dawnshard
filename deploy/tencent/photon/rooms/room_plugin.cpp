#include "battle_session.hpp"
#include "room_data.hpp"
#include "state_publisher.hpp"

#include <chrono>
#include <format>
#include <luxon/common_codes.hpp>
#include <luxon/server/game.hpp>
#include <luxon/server/game_plugin_registry.hpp>
#include <luxon/server/peer_persistence.hpp>
#include <map>
#include <random>
#include <set>

namespace server::game_plugins {
namespace {
std::set<int> room_ids;
int reserve_room_id() {
    static std::mt19937 random(std::random_device{}());
    std::uniform_int_distribution<int> ids(1000000, 9999999);
    int result;
    do {
        result = ids(random);
    } while (room_ids.contains(result));
    room_ids.insert(result);
    return result;
}
} // namespace

// Ordinary quest loading and results use the existing game API through a
// private bridge.
class DawnshardRoomPlugin : public PluginBase {
  public:
    DawnshardRoomPlugin(Game *game, std::string_view name) : PluginBase(game, name) {}
    ~DawnshardRoomPlugin() override {
        if (room_id_)
            room_ids.erase(room_id_);
    }

    Result OnCreateGame(ser::OperationRequestMessage &request, OnCreateGameCallInfo &) override {
        try {
            auto &actor = dawnshard::request_properties(request, DictKeyCodes::Properties::ActorProperties);
            dawnshard::player(1, actor);
            auto &properties =
                dawnshard::request_properties(request, DictKeyCodes::Properties::GameProperties);
            room_id_ = reserve_room_id();
            snapshot_ = dawnshard::room(game_->id, room_id_, properties);
            snapshot_["StartEntryTime"] = std::format(
                "{:%FT%TZ}", std::chrono::floor<std::chrono::seconds>(std::chrono::system_clock::now()));
            properties[std::string("RoomId")] = int32_t(room_id_);
            properties[uint8_t(GameProps::MaxPlayers)] = uint8_t(4);
            game_->empty_game_ttl = 500;
            battle_ = std::make_shared<dawnshard::BattleSession>(
                game_, log_, [this] { dawnshard::StatePublisher::instance().close(game_->id); },
                [this] { reopen(); });
            log_->info("Room plugin initialized (ordinary co-op quests)");
            return Result::Continue;
        } catch (const std::exception &) {
            log_->warn("Rejected invalid room creation properties");
            return Result::Fail;
        }
    }

    Result BeforeJoin(ser::OperationRequestMessage &request, BeforeJoinGameCallInfo &info) override {
        // The Master reserved a slot before this callback. v1.5 does not release
        // that reservation when a plugin rejects the player.
        auto reject = [&] {
            if (info.joiner->persistent)
                game_->expected_users.erase(info.joiner->persistent->user_id);
            return Result::Fail;
        };
        if (closed_ || (battle_ && battle_->active()) || !players_.contains(1) || players_.size() >= 4)
            return reject();
        try {
            auto &props = dawnshard::request_properties(request, DictKeyCodes::Properties::ActorProperties);
            auto candidate = dawnshard::player(0, props);
            for (const auto &[id, existing] : players_)
                if (existing["ViewerId"] == candidate["ViewerId"])
                    return reject();
            return Result::Continue;
        } catch (const std::exception &) {
            log_->warn("Rejected invalid joining player properties");
            return reject();
        }
    }

    Result OnJoinGame(ser::OperationRequestMessage &request, OnJoinGameCallInfo &info) override {
        try {
            // v1.5 calls this hook before copying ActorProperties into the stored
            // GamePeer.
            auto &props = dawnshard::request_properties(request, DictKeyCodes::Properties::ActorProperties);
            int actor_id = info.joiner->actor_id;
            players_[actor_id] = dawnshard::player(actor_id, props);
            if (!publish()) {
                players_.erase(actor_id);
                return Result::Fail;
            }
            log_->info("Room preparation: {} player(s)", players_.size());
            return Result::Continue;
        } catch (const std::exception &) {
            log_->warn("Room preparation could not initialize player");
            return Result::Fail;
        }
    }

    Result OnLeave(ser::OperationRequestMessage &, OnLeaveGameCallInfo &info) override {
        if (!info.leaver)
            return Result::Continue;
        players_.erase(info.leaver->actor_id);
        if (battle_)
            battle_->on_leave(info.leaver->actor_id);
        if (info.leaver->actor_id == 1) {
            closed_ = true;
            game_->is_open = false;
            game_->is_visible = false;
            // MessagePack [RaiseEventSequenceId=1, Reason=HostDisconnected].
            Event broken;
            broken.code = 0x17;
            broken.sender_actor_id = 0;
            broken.receivers = uint8_t(ReceiverGroup::All);
            broken.data = ser::ByteArray{0x92, 0x01, 0x01};
            game_->broadcast_event(broken);
            dawnshard::StatePublisher::instance().close(game_->id);
        } else if (!closed_)
            publish();
        return Result::Continue;
    }

    Result BeforeSetProperties(ser::OperationRequestMessage &, BeforeSetPropertiesCallInfo &info) override {
        auto reject = [&](std::string_view reason) {
            if (battle_ && info.update)
                battle_->trace_properties(info.setter ? info.setter->actor_id : 0, info.target_actor_id,
                                          *info.update, reason);
            return Result::Fail;
        };
        if (!info.setter || !info.update)
            return reject("missing-setter-or-update");
        try {
            const auto &update = *info.update;
            if (info.target_actor_id == 0) {
                if (battle_ && battle_->active())
                    return reject("room-update-during-battle");
                if (info.setter->actor_id != 1)
                    return reject("room-update-by-guest");
                if (update.contains(std::string("RoomId")) ||
                    !dawnshard::property(update, "GoToIngameInfo").is_null())
                    return reject("server-owned-room-property");
                auto merged = game_->custom_props;
                for (const auto &[key, value] : update)
                    merged[key] = value;
                dawnshard::room(game_->id, room_id_, merged);
                if (!dawnshard::property(update, "RoomEntryCondition").is_null())
                    dawnshard::entry_conditions(dawnshard::property(update, "RoomEntryCondition"));
                if (!dawnshard::property(update, "MatchingType").is_null()) {
                    auto type = dawnshard::integer(dawnshard::property(update, "MatchingType"));
                    if (type != 1 && type != 2 && type != 4 && type != 5 && type != 6)
                        return reject("invalid-matching-type");
                }
            } else {
                if (info.target_actor_id != info.setter->actor_id)
                    return reject("another-actor-property");
                for (const auto key : {"PlayerId", "ViewerId"})
                    if (update.contains(std::string(key)))
                        return reject("server-owned-actor-property");
                const auto &start = dawnshard::property(update, "GoToIngameState");
                if (!start.is_null() &&
                    (dawnshard::integer(start) < 0 || dawnshard::integer(start) > 4 || !battle_ ||
                     !battle_->validate_stage(info.target_actor_id, int(dawnshard::integer(start))))) {
                    return reject("invalid-loading-stage");
                }
                if (!dawnshard::property(update, "UsePartySlot").is_null()) {
                    if (battle_ && battle_->active())
                        return reject("party-change-during-battle");
                    auto merged = info.setter->actor_props;
                    for (const auto &[key, value] : update)
                        merged[key] = value;
                    dawnshard::player(info.target_actor_id, merged);
                }
            }
            if (closed_)
                return reject("room-closed");
            if (battle_)
                battle_->trace_properties(info.setter->actor_id, info.target_actor_id, update, "allow");
            return Result::Continue;
        } catch (const std::exception &) {
            return reject("invalid-property-shape");
        }
    }

    Result OnSetProperties(ser::OperationRequestMessage &, OnSetPropertiesCallInfo &info) override {
        try {
            if (info.target_actor_id == 0) {
                // The current game reflects successful compare-and-swap updates only.
                auto current = dawnshard::room(game_->id, room_id_, game_->custom_props);
                current["StartEntryTime"] = snapshot_["StartEntryTime"];
                snapshot_ = std::move(current);
            } else if (auto peer = game_->find_peer(info.target_actor_id)) {
                players_[info.target_actor_id] = dawnshard::player(info.target_actor_id, peer->actor_props);
                const auto &stage = dawnshard::property(*info.update, "GoToIngameState");
                if (battle_ && !stage.is_null() &&
                    dawnshard::property(peer->actor_props, "GoToIngameState") == stage)
                    battle_->on_stage(info.target_actor_id, int(dawnshard::integer(stage)));
            }
            if (!closed_)
                publish();
        } catch (const std::exception &) {
            log_->warn("Room property snapshot could not be updated");
        }
        return Result::Continue;
    }

    Result OnRaiseEvent(ser::OperationRequestMessage &, OnRaiseEventCallInfo &info) override {
        auto result = [&](Result outcome) {
            if (battle_)
                battle_->trace_event(info.event, outcome == Result::Continue ? "relay" : "cancel");
            return outcome;
        };
        // Server-only events cannot be forged by a client.
        switch (info.event.code) {
        case 0x14:
        case 0x15:
        case 0x17:
        case 0x3e:
        case 0x40:
        case 0x44:
            return result(Result::Cancel);
        case 0x03:
        case 0x18:
        case 0x3f:
        case 0x43:
        case 0x48:
            return result(!closed_ && battle_ && info.raiser && battle_->on_event(*info.raiser, info.event)
                              ? Result::Continue
                              : Result::Cancel);
        default:
            return result(closed_ ? Result::Cancel : Result::Continue);
        }
    }

    Result OnCloseGame(OnCloseGameCallInfo &) override {
        dawnshard::StatePublisher::instance().close(game_->id);
        return Result::Continue;
    }

  private:
    void reopen() {
        int previous_room_id = room_id_;
        room_id_ = reserve_room_id();
        room_ids.erase(previous_room_id);
        game_->is_open = true;
        game_->is_visible = true;
        ser::Hashtable properties{{std::string("RoomId"), int32_t(room_id_)}};
        game_->insert_game_props(properties);
        auto event = game_->create_property_update_event(0, properties, 0);
        event.receivers = uint8_t(ReceiverGroup::All);
        game_->broadcast_event(event);
        snapshot_ = dawnshard::room(game_->id, room_id_, game_->custom_props);
        snapshot_["StartEntryTime"] = std::format(
            "{:%FT%TZ}", std::chrono::floor<std::chrono::seconds>(std::chrono::system_clock::now()));
        publish();
    }

    bool publish() {
        if (battle_ && battle_->active())
            return true;
        snapshot_["Players"] = dawnshard::Json::array();
        for (const auto &[id, player] : players_)
            snapshot_["Players"].push_back(player);
        return dawnshard::StatePublisher::instance().publish(game_->id, snapshot_);
    }
    int room_id_ = 0;
    std::shared_ptr<dawnshard::BattleSession> battle_;
    bool closed_ = false;
    dawnshard::Json snapshot_;
    std::map<int, dawnshard::Json> players_;
};

LN_REGISTER_GAME_PLUGIN("GluonPlugin", DawnshardRoomPlugin)
} // namespace server::game_plugins
