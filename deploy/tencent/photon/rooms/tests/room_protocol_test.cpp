#include "room_data.hpp"
#include <iostream>
#include <lz4.h>
#include <stdexcept>

void require(bool condition, const char *message) {
    if (!condition)
        throw std::runtime_error(message);
}

int main() {
    using namespace dawnshard;
    Json conditions = Json::array({Json::array({2, 3, 4, 5}), Json::array({1, 2}), 11700, Json::array({1})});
    auto bytes = Json::to_msgpack(conditions);
    auto plain = entry_conditions(ser::Value(bytes));
    require(plain["RequiredPartyPower"] == 11700 && plain["ObjectiveTextId"] == 1,
            "Plain MessagePack conditions");
    std::vector<uint8_t> compressed(LZ4_compressBound(bytes.size()) + 5);
    compressed[0] = 0xd2;
    uint32_t size = bytes.size();
    for (int i = 0; i < 4; ++i)
        compressed[i + 1] = (size >> (24 - i * 8)) & 255;
    int count = LZ4_compress_default(reinterpret_cast<const char *>(bytes.data()),
                                     reinterpret_cast<char *>(compressed.data() + 5), bytes.size(),
                                     compressed.size() - 5);
    compressed.resize(count + 5);
    auto extension = Json::to_msgpack(Json::binary(compressed, 99));
    require(entry_conditions(ser::Value(extension)) == plain, "LZ4 MessagePack conditions");
    for (const auto &invalid :
         {ser::ByteArray{}, ser::ByteArray{0x91, 0xc0}, ser::ByteArray{0xc7, 1, 99, 0xd2}}) {
        bool rejected = false;
        try {
            entry_conditions(ser::Value(invalid));
        } catch (const std::exception &) {
            rejected = true;
        }
        require(rejected, "Malformed conditions rejected");
    }
    ser::Hashtable actor{{std::string("PlayerId"), std::string("1234")},
                         {std::string("UsePartySlot"), std::vector<int32_t>{1}}};
    auto record = player(2, actor);
    require(record["ViewerId"] == 1234 && record["ActorNr"] == 2, "Player JSON");
    require(property(actor, "ViewerId").is_equal(std::string("1234")),
            "ViewerId preserves Photon string type");
    bool rejected = false;
    try {
        integer(ser::Value(std::string("123junk")));
    } catch (const std::exception &) {
        rejected = true;
    }
    require(rejected, "Partial integer rejected");
    std::cout << "Room protocol checks passed\n";
}
