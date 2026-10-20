#include "state_publisher.hpp"

#include <cstdlib>
#include <curl/curl.h>
#include <fstream>
#include <iostream>

namespace dawnshard {
StatePublisher &StatePublisher::instance() {
    static StatePublisher publisher;
    return publisher;
}

StatePublisher::StatePublisher() {
    const char *url = std::getenv("LUXON_STATE_URL");
    const char *token_file = std::getenv("LUXON_STATE_TOKEN_FILE");
    if (url && token_file) {
        url_ = url;
        std::ifstream file(token_file);
        std::getline(file, token_);
    }
    // This adapter only calls the local service. It never follows redirects or proxies.
    if (!url_.starts_with("http://127.0.0.1:") || token_.empty() || token_.size() > 512 ||
        token_.find_first_of("\r\n") != std::string::npos) {
        url_.clear();
        token_.clear();
        std::cerr << "[Rooms] Local state service configuration is missing or invalid\n";
        return;
    }
    curl_global_init(CURL_GLOBAL_DEFAULT);
    worker_ = std::jthread([this] { run(); });
}

StatePublisher::~StatePublisher() {
    {
        std::lock_guard lock(mutex_);
        stopping_ = true;
    }
    wake_.notify_one();
    if (worker_.joinable())
        worker_.join();
}

bool StatePublisher::publish(const std::string &name, Json game) {
    if (url_.empty())
        return false;
    std::lock_guard lock(mutex_);
    if (!entries_.contains(name) && entries_.size() >= 64)
        return false;
    auto &entry = entries_[name];
    entry.body = Json{{"Game", std::move(game)}}.dump();
    entry.closed = false;
    ++entry.revision;
    entry.due = Clock::now();
    wake_.notify_one();
    return true;
}

void StatePublisher::close(const std::string &name) {
    std::lock_guard lock(mutex_);
    auto it = entries_.find(name);
    if (it == entries_.end())
        return;
    it->second.body = Json{{"GameName", name}}.dump();
    it->second.closed = true;
    ++it->second.revision;
    it->second.due = Clock::now();
    wake_.notify_one();
}

void StatePublisher::run() {
    CURL *curl = curl_easy_init();
    if (!curl)
        return;
    curl_slist *headers = curl_slist_append(nullptr, "Content-Type: application/json");
    headers = curl_slist_append(headers, ("Authorization: Bearer " + token_).c_str());
    curl_easy_setopt(curl, CURLOPT_HTTPHEADER, headers);
    curl_easy_setopt(curl, CURLOPT_PROXY, "");
    curl_easy_setopt(curl, CURLOPT_PROTOCOLS_STR, "http");
    curl_easy_setopt(curl, CURLOPT_FOLLOWLOCATION, 0L);
    curl_easy_setopt(curl, CURLOPT_CONNECTTIMEOUT_MS, 250L);
    curl_easy_setopt(curl, CURLOPT_TIMEOUT_MS, 1000L);
    curl_easy_setopt(curl, CURLOPT_NOSIGNAL, 1L);
    curl_easy_setopt(
        curl, CURLOPT_WRITEFUNCTION,
        +[](char *, size_t size, size_t n, void *) -> size_t { return size * n; });
    bool failed = false;
    std::unique_lock lock(mutex_);
    while (!stopping_) {
        auto selected = entries_.end();
        for (auto it = entries_.begin(); it != entries_.end(); ++it)
            if (selected == entries_.end() || it->second.due < selected->second.due)
                selected = it;
        if (selected == entries_.end()) {
            wake_.wait(lock);
            continue;
        }
        if (selected->second.due > Clock::now()) {
            wake_.wait_until(lock, selected->second.due);
            continue;
        }
        std::string name = selected->first;
        Entry pending = selected->second;
        lock.unlock();
        std::string endpoint = url_ + (pending.closed ? "/Event/GameClose" : "/Event/LuxonSnapshot");
        curl_easy_setopt(curl, CURLOPT_URL, endpoint.c_str());
        curl_easy_setopt(curl, CURLOPT_POSTFIELDS, pending.body.c_str());
        curl_easy_setopt(curl, CURLOPT_POSTFIELDSIZE, static_cast<long>(pending.body.size()));
        CURLcode code = curl_easy_perform(curl);
        long status = 0;
        curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &status);
        bool success = code == CURLE_OK && status == 200;
        if (!success && !failed)
            std::cerr << "[Rooms] State sync failed; retrying latest room snapshot\n";
        if (success && failed)
            std::cerr << "[Rooms] State sync recovered\n";
        failed = !success;
        lock.lock();
        auto current = entries_.find(name);
        if (current == entries_.end() || current->second.revision != pending.revision)
            continue;
        if (success && pending.closed)
            entries_.erase(current);
        else
            current->second.due = Clock::now() + std::chrono::seconds(success ? 15 : 1);
    }
    curl_slist_free_all(headers);
    curl_easy_cleanup(curl);
}
} // namespace dawnshard
