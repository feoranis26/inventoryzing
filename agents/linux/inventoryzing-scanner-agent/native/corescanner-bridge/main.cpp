// inventoryzing's narrow bridge for Zebra Scanner SDK for Linux.
// The vendor SDK owns USB/CoreScanner communication. Stdout is NDJSON; stdin
// accepts tab-delimited commands from the managed scanner host.

#include "CsBarcodeTypes.h"
#include "CsIEventListenerXml.h"
#include "CsUserDefs.h"
#include "Cslibcorescanner_xml.h"

#include <chrono>
#include <iostream>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

namespace {
std::mutex output_gate;
std::mutex core_gate;

std::string escape_json(const std::string& value) {
    std::ostringstream escaped;
    for (const unsigned char ch : value) {
        switch (ch) {
            case '"': escaped << "\\\""; break;
            case '\\': escaped << "\\\\"; break;
            case '\b': escaped << "\\b"; break;
            case '\f': escaped << "\\f"; break;
            case '\n': escaped << "\\n"; break;
            case '\r': escaped << "\\r"; break;
            case '\t': escaped << "\\t"; break;
            default:
                if (ch < 0x20) {
                    static constexpr char hex[] = "0123456789abcdef";
                    escaped << "\\u00" << hex[ch >> 4] << hex[ch & 0x0f];
                } else {
                    escaped << ch;
                }
        }
    }
    return escaped.str();
}

void emit_xml(const char* type, short event_type, const std::string& xml) {
    std::lock_guard lock(output_gate);
    std::cout << "{\"type\":\"" << type << "\",\"eventType\":" << event_type
              << ",\"xml\":\"" << escape_json(xml) << "\"}" << std::endl;
}

void emit_error(const std::string& message) {
    std::lock_guard lock(output_gate);
    std::cout << "{\"type\":\"error\",\"message\":\"" << escape_json(message)
              << "\"}" << std::endl;
}

std::vector<std::string> split_tabs(const std::string& input) {
    std::vector<std::string> values;
    std::stringstream stream(input);
    std::string value;
    while (std::getline(stream, value, '\t')) values.push_back(value);
    return values;
}

std::string action_xml(const std::string& scanner_id, int action) {
    return "<inArgs><scannerID>" + scanner_id +
        "</scannerID><cmdArgs><arg-xml><attrib_list><attribute><id>6000</id>"
        "<datatype>X</datatype><value>" + std::to_string(action) +
        "</value></attribute></attrib_list></arg-xml></cmdArgs></inArgs>";
}

class BridgeListener final : public IEventListenerXml {
public:
    void OnImageEvent(short, int, short, char*, int, std::string&) override {}
    void OnVideoEvent(short, int, char*, int, std::string&) override {}
    void OnBarcodeEvent(short event_type, std::string& data) override { emit_xml("barcode", event_type, data); }
    void OnPNPEvent(short event_type, std::string data) override { emit_xml("pnp", event_type, data); }
    void OnCommandResponseEvent(short, std::string&) override {}
    void OnScannerNotification(short, std::string&) override {}
    void OnIOEvent(short, unsigned char) override {}
    void OnScanRMDEvent(short, std::string&) override {}
    void OnDisconnect() override { emit_error("CoreScanner daemon disconnected."); }
    void OnBinaryDataEvent(short, int, short, unsigned char*, std::string&) override {}

    bool open() {
        StatusID status;
        // Inventoryzing accepts only the CoreScanner SNAPI transport. Opening all
        // transports would let an unrelated keyboard-mode scanner create actions.
        ::Open(this, SCANNER_TYPE_SNAPI, &status);
        if (status != STATUS_OK) {
            emit_error("Unable to open Zebra CoreScanner daemon (status " + std::to_string(status) + ").");
            return false;
        }
        std::string output;
        std::string events = "<inArgs><cmdArgs><arg-int>2</arg-int><arg-int>1,16</arg-int></cmdArgs></inArgs>";
        ::ExecCommand(CMD_REGISTER_FOR_EVENTS, events, output, &status);
        if (status != STATUS_OK) {
            emit_error("Unable to register CoreScanner events (status " + std::to_string(status) + ").");
            return false;
        }
        emit_scanners();
        return true;
    }

    void close() {
        std::lock_guard lock(core_gate);
        StatusID status;
        ::Close(0, &status);
    }

    void emit_scanners() {
        std::lock_guard lock(core_gate);
        unsigned short count = 0;
        std::vector<unsigned int> scanner_ids;
        std::string xml;
        StatusID status;
        ::GetScanners(&count, &scanner_ids, xml, &status);
        if (status == STATUS_OK) emit_xml("scanners", 0, xml);
        else emit_error("Unable to enumerate scanners (status " + std::to_string(status) + ").");
    }

    void emit_topology() {
        std::lock_guard lock(core_gate);
        std::string output;
        StatusID status;
        std::string input = "<inArgs></inArgs>";
        ::ExecCommand(static_cast<CmdOpcode>(5006), input, output, &status);
        if (status == STATUS_OK) emit_xml("topology", 0, output);
        else emit_error("Unable to read scanner topology (status " + std::to_string(status) + ").");
    }

    void feedback(const std::vector<std::string>& fields) {
        if (fields.size() != 6) {
            emit_error("Invalid feedback command.");
            return;
        }
        const auto& tone_target = fields[1];
        const auto& led_target = fields[2];
        const auto& color = fields[3];
        const auto& tone = fields[4];
        int duration = 0;
        try { duration = std::stoi(fields[5]); }
        catch (const std::exception&) { emit_error("Invalid feedback duration."); return; }

        const int beep = tone == "Rising" || tone == "RisingDoubleHigh" ? 23 :
            tone == "Falling" ? 22 : tone == "DoubleShort" ? 1 :
            tone == "DoubleLowShort" ? 6 : -1;
        const int led_on = color == "Green" ? 43 : color == "Amber" ? 45 : color == "Red" ? 47 : -1;
        const int led_off = color == "Green" ? 42 : color == "Amber" ? 46 : color == "Red" ? 48 : -1;
        if (beep < 0 || led_on < 0 || duration < 0 || duration > 5000) {
            emit_error("Invalid feedback values.");
            return;
        }

        std::lock_guard lock(core_gate);
        StatusID status;
        std::string output;
        auto input = action_xml(tone_target, beep);
        ::ExecCommand(CMD_RSM_ATTR_SET, input, output, &status);
        if (status != STATUS_OK) { emit_error("Beeper command failed (status " + std::to_string(status) + ")."); return; }
        if (tone == "RisingDoubleHigh") {
            std::this_thread::sleep_for(std::chrono::milliseconds(300));
            input = action_xml(tone_target, 1);
            ::ExecCommand(CMD_RSM_ATTR_SET, input, output, &status);
            if (status != STATUS_OK) { emit_error("Second beeper command failed (status " + std::to_string(status) + ")."); return; }
        }
        if (led_target.empty()) return;
        input = action_xml(led_target, led_on);
        ::ExecCommand(CMD_RSM_ATTR_SET, input, output, &status);
        if (status != STATUS_OK) { emit_error("LED-on command failed (status " + std::to_string(status) + ")."); return; }
        std::this_thread::sleep_for(std::chrono::milliseconds(duration));
        input = action_xml(led_target, led_off);
        ::ExecCommand(CMD_RSM_ATTR_SET, input, output, &status);
        if (status != STATUS_OK) emit_error("LED-off command failed (status " + std::to_string(status) + ").");
    }
};
}  // namespace

int main() {
    BridgeListener listener;
    if (!listener.open()) return 1;

    std::string command;
    while (std::getline(std::cin, command)) {
        const auto fields = split_tabs(command);
        if (fields.empty()) continue;
        if (fields[0] == "feedback") listener.feedback(fields);
        else if (fields[0] == "topology") listener.emit_topology();
        else if (fields[0] == "scanners") listener.emit_scanners();
        else emit_error("Unknown bridge command.");
    }
    listener.close();
    return 0;
}
