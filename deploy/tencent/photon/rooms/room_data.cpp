#include "room_data.hpp"

#include <charconv>
#include <limits>
#include <lz4.h>
#include <stdexcept>

namespace dawnshard {
const ser::Value &property(const ser::Hashtable &properties, const std::string &key) {
    auto it = properties.find(key);
    return it == properties.end() ? ser::null : it->second;
}

int64_t integer(const ser::Value &value) {
    if (auto p = value.get_ptr<int32_t>())
        return *p;
    if (auto p = value.get_ptr<int64_t>())
        return *p;
    if (auto p = value.get_ptr<uint8_t>())
        return *p;
    if (auto p = value.get_ptr<int16_t>())
        return *p;
    if (auto p = value.get_ptr<std::string>()) {
        int64_t result{};
        auto [end, ec] = std::from_chars(p->data(), p->data() + p->size(), result);
        if (ec == std::errc{} && end == p->data() + p->size())
            return result;
    }
    throw std::invalid_argument("Missing or invalid integer property");
}

ser::Hashtable &request_properties(ser::OperationRequestMessage &request, uint8_t key) {
    auto value = request.parameters[key].get_ptr<ser::HashtablePtr>();
    if (!value || !*value)
        throw std::invalid_argument("Missing properties");
    return **value;
}

Json entry_conditions(const ser::Value &value) {
    Json result = {{"UnacceptedElementTypeList", Json::array()},
                   {"UnacceptedWeaponTypeList", Json::array()},
                   {"RequiredPartyPower", 0},
                   {"ObjectiveTextId", 0}};
    if (value.is_null())
        return result;
    const auto &bytes = value.get<ser::ByteArray>();
    if (bytes.empty() || bytes.size() > 1024)
        throw std::invalid_argument("Invalid conditions size");
    Json data = Json::from_msgpack(bytes);
    // MessagePack-CSharp Lz4Block: extension 99, int32 uncompressed size, raw LZ4 block.
    if (data.is_binary()) {
        const auto &block = data.get_binary();
        if (block.subtype() != 99 || block.size() < 6 || block[0] != 0xd2)
            throw std::invalid_argument("Unsupported conditions extension");
        uint32_t size =
            (uint32_t(block[1]) << 24) | (uint32_t(block[2]) << 16) | (uint32_t(block[3]) << 8) | block[4];
        if (size == 0 || size > 1024)
            throw std::invalid_argument("Invalid decoded size");
        ser::ByteArray decoded(size);
        int count = LZ4_decompress_safe(reinterpret_cast<const char *>(block.data() + 5),
                                        reinterpret_cast<char *>(decoded.data()),
                                        static_cast<int>(block.size() - 5), static_cast<int>(size));
        if (count != static_cast<int>(size))
            throw std::invalid_argument("Invalid LZ4 block");
        data = Json::from_msgpack(decoded);
    }
    if (!data.is_array() || data.size() != 4 || !data[0].is_array() || !data[1].is_array() ||
        !data[2].is_number_integer() || !data[3].is_array() || data[3].size() != 1 ||
        !data[3][0].is_number_integer())
        throw std::invalid_argument("Invalid conditions shape");
    for (const auto index : {0, 1}) {
        if (data[index].size() > 16)
            throw std::invalid_argument("Too many conditions");
        for (const auto &item : data[index])
            if (!item.is_number_integer())
                throw std::invalid_argument("Invalid condition");
    }
    result["UnacceptedElementTypeList"] = data[0];
    result["UnacceptedWeaponTypeList"] = data[1];
    result["RequiredPartyPower"] = data[2];
    result["ObjectiveTextId"] = data[3][0];
    return result;
}

Json player(int actor_id, ser::Hashtable &properties) {
    const auto &id_property = property(properties, "PlayerId");
    auto viewer_id = integer(id_property);
    if (viewer_id <= 0)
        throw std::invalid_argument("Invalid player ID");
    // The game expects the original Photon type here (usually a string).
    properties[std::string("ViewerId")] = id_property;
    const auto &slots = property(properties, "UsePartySlot").get<std::vector<int32_t>>();
    if (slots.empty() || slots.size() > 4)
        throw std::invalid_argument("Invalid party slots");
    return {{"ActorNr", actor_id}, {"ViewerId", viewer_id}, {"PartyNoList", slots}};
}

Json room(const std::string &name, int room_id, const ser::Hashtable &properties) {
    auto quest = integer(property(properties, "C0"));
    auto compatible = integer(property(properties, "MatchingCompatibleId"));
    auto matching = integer(property(properties, "MatchingType"));
    if (name.empty() || name.size() > 128 || quest <= 0 || quest > INT32_MAX || compatible < 0 ||
        compatible > INT32_MAX ||
        (matching != 1 && matching != 2 && matching != 4 && matching != 5 && matching != 6))
        throw std::invalid_argument("Invalid room properties");
    return {{"Name", name},
            {"RoomId", room_id},
            {"QuestId", quest},
            {"MatchingCompatibleId", compatible},
            {"MatchingType", matching},
            {"EntryConditions", entry_conditions(property(properties, "RoomEntryCondition"))},
            {"Players", Json::array()}};
}
} // namespace dawnshard
