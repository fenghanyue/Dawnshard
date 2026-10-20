#pragma once

#include "room_data.hpp"
#include <condition_variable>
#include <map>
#include <mutex>
#include <thread>

namespace dawnshard {
// One worker owns all HTTP I/O. The game loop only replaces immutable snapshots.
// A retry always sends the latest snapshot; no join/leave deltas can be reordered.
class StatePublisher {
  public:
    static StatePublisher &instance();
    bool publish(const std::string &name, Json game);
    void close(const std::string &name);
    ~StatePublisher();

  private:
    using Clock = std::chrono::steady_clock;
    struct Entry {
        std::string body;
        bool closed = false;
        uint64_t revision = 0;
        Clock::time_point due{};
    };
    StatePublisher();
    void run();
    std::mutex mutex_;
    std::condition_variable wake_;
    std::map<std::string, Entry> entries_;
    std::jthread worker_;
    std::string url_, token_;
    bool stopping_ = false;
};
} // namespace dawnshard
