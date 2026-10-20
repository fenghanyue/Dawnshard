#include "battle_http.hpp"

#include <curl/curl.h>
#include <fstream>
#include <lz4.h>
#include <stdexcept>

namespace dawnshard {
namespace {
constexpr std::string_view alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
constexpr size_t max_reply = 4 * 1024 * 1024;
} // namespace

std::string base64_encode(const ser::ByteArray &bytes) {
    std::string result;
    result.reserve((bytes.size() + 2) / 3 * 4);
    for (size_t i = 0; i < bytes.size(); i += 3) {
        uint32_t n = uint32_t(bytes[i]) << 16;
        if (i + 1 < bytes.size())
            n |= uint32_t(bytes[i + 1]) << 8;
        if (i + 2 < bytes.size())
            n |= bytes[i + 2];
        result += alphabet[(n >> 18) & 63];
        result += alphabet[(n >> 12) & 63];
        result += i + 1 < bytes.size() ? alphabet[(n >> 6) & 63] : '=';
        result += i + 2 < bytes.size() ? alphabet[n & 63] : '=';
    }
    return result;
}

ser::ByteArray base64_decode(const std::string &text) {
    if (text.size() % 4 || text.size() > max_reply * 2)
        throw std::invalid_argument("Invalid base64 size");
    ser::ByteArray result;
    result.reserve(text.size() / 4 * 3);
    for (size_t i = 0; i < text.size(); i += 4) {
        uint32_t n = 0;
        int padding = 0;
        for (size_t j = 0; j < 4; ++j) {
            char c = text[i + j];
            auto index = alphabet.find(c);
            if (c == '=' && i + 4 == text.size() && j >= 2) {
                ++padding;
                index = 0;
            } else if (index == std::string_view::npos || padding)
                throw std::invalid_argument("Invalid base64 data");
            n = (n << 6) | uint32_t(index);
        }
        result.push_back(uint8_t(n >> 16));
        if (padding < 2)
            result.push_back(uint8_t(n >> 8));
        if (padding < 1)
            result.push_back(uint8_t(n));
    }
    return result;
}

Json decode_messagepack(const ser::ByteArray &bytes, size_t max_size) {
    if (bytes.empty() || bytes.size() > max_size)
        throw std::invalid_argument("Invalid event size");
    Json data = Json::from_msgpack(bytes);
    if (!data.is_binary())
        return data;
    const auto &block = data.get_binary();
    if (block.subtype() != 99 || block.size() < 6 || block[0] != 0xd2)
        throw std::invalid_argument("Unsupported event compression");
    uint32_t size =
        (uint32_t(block[1]) << 24) | (uint32_t(block[2]) << 16) | (uint32_t(block[3]) << 8) | block[4];
    if (!size || size > max_size)
        throw std::invalid_argument("Decoded event is too large");
    ser::ByteArray decoded(size);
    if (LZ4_decompress_safe(reinterpret_cast<const char *>(block.data() + 5),
                            reinterpret_cast<char *>(decoded.data()), int(block.size() - 5),
                            int(size)) != int(size))
        throw std::invalid_argument("Invalid compressed event");
    return Json::from_msgpack(decoded);
}

BattleHttp &BattleHttp::instance() {
    static BattleHttp http;
    return http;
}

BattleHttp::BattleHttp() {
    const char *url = std::getenv("LUXON_STATE_URL");
    const char *file = std::getenv("LUXON_STATE_TOKEN_FILE");
    if (url && file) {
        url_ = url;
        std::ifstream stream(file);
        std::getline(stream, token_);
    }
    if (!url_.starts_with("http://127.0.0.1:") || token_.empty() || token_.size() > 512 ||
        token_.find_first_of("\r\n") != std::string::npos) {
        url_.clear();
        token_.clear();
    }
    curl_global_init(CURL_GLOBAL_DEFAULT);
    for (int i = 0; i < 2; ++i)
        workers_.emplace_back([this] { run(); });
}

BattleHttp::~BattleHttp() {
    {
        std::lock_guard lock(mutex_);
        stopping_ = true;
        jobs_.clear();
    }
    wake_.notify_all();
    workers_.clear();
}

std::future<BattleReply> BattleHttp::post(const std::string &operation, Json body) {
    Job job{operation, body.dump(), {}};
    auto future = job.promise.get_future();
    {
        std::lock_guard lock(mutex_);
        if (url_.empty() || jobs_.size() >= 64 || (operation != "Prepare" && operation != "Clear")) {
            job.promise.set_value({});
            return future;
        }
        jobs_.push_back(std::move(job));
    }
    wake_.notify_one();
    return future;
}

void BattleHttp::run() {
    while (true) {
        Job job;
        {
            std::unique_lock lock(mutex_);
            wake_.wait(lock, [this] { return stopping_ || !jobs_.empty(); });
            if (stopping_)
                return;
            job = std::move(jobs_.front());
            jobs_.pop_front();
        }
        BattleReply result;
        CURL *curl = curl_easy_init();
        if (curl) {
            std::string response;
            std::string endpoint = url_ + "/Luxon/" + job.path;
            curl_slist *headers = curl_slist_append(nullptr, "Content-Type: application/json");
            headers = curl_slist_append(headers, ("Authorization: Bearer " + token_).c_str());
            curl_easy_setopt(curl, CURLOPT_HTTPHEADER, headers);
            curl_easy_setopt(curl, CURLOPT_URL, endpoint.c_str());
            curl_easy_setopt(curl, CURLOPT_PROXY, "");
            curl_easy_setopt(curl, CURLOPT_PROTOCOLS_STR, "http");
            curl_easy_setopt(curl, CURLOPT_FOLLOWLOCATION, 0L);
            curl_easy_setopt(curl, CURLOPT_CONNECTTIMEOUT_MS, 1000L);
            curl_easy_setopt(curl, CURLOPT_TIMEOUT_MS, 15000L);
            curl_easy_setopt(curl, CURLOPT_NOSIGNAL, 1L);
            curl_easy_setopt(curl, CURLOPT_POSTFIELDS, job.body.c_str());
            curl_easy_setopt(curl, CURLOPT_POSTFIELDSIZE, long(job.body.size()));
            curl_easy_setopt(curl, CURLOPT_WRITEDATA, &response);
            curl_easy_setopt(
                curl, CURLOPT_WRITEFUNCTION,
                +[](char *data, size_t size, size_t count, void *target) -> size_t {
                    auto &body = *static_cast<std::string *>(target);
                    size_t length = size * count;
                    if (body.size() + length > max_reply)
                        return 0;
                    body.append(data, length);
                    return length;
                });
            if (curl_easy_perform(curl) == CURLE_OK) {
                curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &result.status);
                if (result.status == 200)
                    result.body = Json::parse(response, nullptr, false);
            }
            curl_slist_free_all(headers);
            curl_easy_cleanup(curl);
        }
        job.promise.set_value(std::move(result));
    }
}
} // namespace dawnshard
