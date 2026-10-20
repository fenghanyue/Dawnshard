#pragma once

#include "json.hpp"
#include <luxon/ser_types.hpp>

namespace dawnshard {
using Json = nlohmann::json;
namespace ser = luxon::ser;

const ser::Value &property(const ser::Hashtable &properties, const std::string &key);
int64_t integer(const ser::Value &value);
ser::Hashtable &request_properties(ser::OperationRequestMessage &request, uint8_t key);
Json entry_conditions(const ser::Value &value);
Json player(int actor_id, ser::Hashtable &properties);
Json room(const std::string &name, int room_id, const ser::Hashtable &properties);
} // namespace dawnshard
