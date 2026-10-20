#pragma once

#include "room_data.hpp"
#include <condition_variable>
#include <deque>
#include <future>
#include <mutex>
#include <thread>

namespace dawnshard {
struct BattleReply {
    long status = 0;
    Json body;
};

// Only performs local authenticated HTTP. Game objects are never touched by
// workers.
class BattleHttp {
  public:
    static BattleHttp &instance();
    std::future<BattleReply> post(const std::string &operation, Json body);
    ~BattleHttp();

  private:
    BattleHttp();
    struct Job {
        std::string path, body;
        std::promise<BattleReply> promise;
    };
    std::string url_, token_;
    std::mutex mutex_;
    std::condition_variable wake_;
    std::deque<Job> jobs_;
    bool stopping_ = false;
    std::vector<std::jthread> workers_;
    void run();
};

std::string base64_encode(const ser::ByteArray &bytes);
ser::ByteArray base64_decode(const std::string &text);
Json decode_messagepack(const ser::ByteArray &bytes, size_t max_size);
} // namespace dawnshard
