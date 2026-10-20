#pragma once

#include "battle_http.hpp"
#include <chrono>
#include <functional>
#include <luxon/server/game.hpp>
#include <map>

namespace dawnshard {
class BattleSession : public std::enable_shared_from_this<BattleSession> {
  public:
    BattleSession(server::Game *game, std::shared_ptr<server::logger> log, std::function<void()> hide,
                  std::function<void()> reopen);
    bool active() const { return active_; }
    bool validate_stage(int actor, int stage) const;
    void on_stage(int actor, int stage);
    void on_leave(int actor);
    bool on_event(server::GamePeer &actor, server::Event &event);

  private:
    struct Member {
        Json actor;
        int stage = 0, count = 0;
        bool ready = false, dead = false, finished = false;
        std::optional<std::future<BattleReply>> record;
        ser::ByteArray response;
    };
    std::weak_ptr<server::Game> game_;
    std::shared_ptr<server::logger> log_;
    std::function<void()> hide_, reopen_;
    std::map<int, Member> members_;
    bool active_ = false, broken_ = false, party_sent_ = false, characters_sent_ = false;
    bool running_ = false, reset_requested_ = false, polling_ = false;
    Json prepared_;
    std::optional<std::future<BattleReply>> preparation_;
    std::chrono::steady_clock::time_point load_deadline_;

    void begin();
    void advance();
    void schedule_poll();
    void poll();
    void clear(server::GamePeer &actor, const ser::ByteArray &payload);
    void fail(server::GamePeer &actor, const ser::ByteArray &payload);
    void maybe_reset();
    void break_room(int reason = 0);
    Json actors() const;
    int minimum_stage() const;
    void emit(uint8_t code, ser::ByteArray bytes, int actor = 0);
    void game_properties(ser::Hashtable properties);
};
} // namespace dawnshard
