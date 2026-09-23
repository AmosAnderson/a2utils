// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

namespace A2Utils.Core.Execution;

/// <summary>Physical memory observations for the Apple IIe/IIc backend in MAME 0.289.</summary>
public static class AppleIIeMemory
{
    /// <summary>Returns whether the pinned machine profile supports physical bank observations.</summary>
    public static bool IsIIeMachine(string? machine) => machine is "apple2e" or "apple2ee" or "apple2c";

    /// <summary>
    /// Validates CPU-visible or physical RAM ranges. CPU observations exclude the I/O/slot-ROM window.
    /// Language-card addresses are CPU addresses; both bank names share their upper 8 KiB.
    /// </summary>
    public static bool IsValidRange(string? bank, int address, int length)
    {
        if (address < 0 || length <= 0 || (long)address + length > 0x10000)
            return false;

        return bank switch
        {
            "cpu" => address >= 0xd000 || (long)address + length <= 0xc000,
            "main" or "aux" => (long)address + length <= 0xc000,
            "lc1" or "lc2" or "aux-lc1" or "aux-lc2" => address >= 0xd000,
            _ => false
        };
    }

    /// <summary>
    /// Creates read_bank(bank, address) and read_video_flag(name) Lua functions. Insert inside the scope
    /// containing machine and memory (the CPU program address space). Physical reads inspect saved RAM
    /// directly, without touching soft switches or changing CPU mappings. Backend layouts are checked lazily.
    /// </summary>
    public static string CreateLuaHelpers() => """
        local bank_items = {}
        local function require_iie()
            local name = machine.system.name
            assert(name == "apple2e" or name == "apple2ee" or name == "apple2c",
                "Physical memory observations require an Apple IIe/IIc machine")
            return name
        end
        local function saved_byte_item(tag, name, expected_count)
            local key = tag .. "/" .. name
            local item = bank_items[key]
            if not item then
                local device = assert(machine.devices[tag], "Missing physical-memory device " .. tag)
                local index = assert(device.items[name], "Missing saved item " .. key)
                item = emu.item(index)
                assert(item.size == 1 and item.count == expected_count,
                    "Unsupported saved item layout " .. key)
                bank_items[key] = item
            end
            return item
        end
        local function physical_read(bank, address)
            local model = require_iie()
            local auxiliary = bank == "aux" or bank == "aux-lc1" or bank == "aux-lc2"
            local language_card = bank == "lc1" or bank == "lc2" or bank == "aux-lc1" or bank == "aux-lc2"
            assert(bank == "main" or bank == "aux" or language_card, "Unknown physical memory bank")
            assert(math.type(address) == "integer" and address >= 0 and address <= 0xffff,
                "Invalid physical memory address")
            assert((language_card and address >= 0xd000) or (not language_card and address < 0xc000),
                "Physical memory address is outside its bank")
            local offset = address
            if (bank == "lc1" or bank == "aux-lc1") and address < 0xe000 then
                offset = address - 0x1000
            end
            local item
            if auxiliary and model ~= "apple2c" then
                item = saved_byte_item(":aux:ext80", "0/m_ram", 0x10000)
            else
                item = saved_byte_item(":ram", "0/m_pointer", model == "apple2c" and 0x20000 or 0x10000)
                if auxiliary then offset = offset + 0x10000 end
            end
            local value = item:read(offset)
            assert(type(value) == "number" and value >= 0 and value <= 255, "Invalid physical RAM value")
            return value
        end
        local function read_bank(bank, address)
            if bank == "cpu" then
                assert(math.type(address) == "integer" and address >= 0 and address <= 0xffff
                    and (address < 0xc000 or address >= 0xd000), "CPU observation touches I/O or an invalid address")
                return memory:read_u8(address)
            end
            return physical_read(bank, address)
        end
        local video_items = {
            altCharset = "m_altcharset", columns80 = "m_80col", page2 = "m_page2", store80 = "m_80store",
            graphics = "m_graphics", mixed = "m_mix", hires = "m_hires", doubleHires = "m_dhires", flash = "m_flash"
        }
        local function read_video_flag(name)
            require_iie()
            local key = assert(video_items[name], "Unknown video flag")
            local value = saved_byte_item(":a2video", "0/" .. key, 1):read(0)
            assert(value == 0 or value == 1, "Invalid video flag " .. name)
            return value
        end
        """;
}
