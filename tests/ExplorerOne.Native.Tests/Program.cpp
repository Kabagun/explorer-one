// Pure input-boundary checks against the production implementation. Including
// its translation unit exposes internal parsers without changing production APIs.
// These tests never call hooks, COM, windows, or native routing entry points.
#include "../../src/ExplorerOne.RouterNative/TargetFolderBridge.cpp"
#include <cstdio>
#include <cstdlib>

int main() {
    unsigned checks = 0;
    const auto check = [&checks](bool condition, const char* name) {
        ++checks;
        if (!condition) { std::fprintf(stderr, "Failed: %s\n", name); std::exit(1); }
    };
    const BYTE root[]{ 0, 0 };
    const BYTE item[]{ 4, 0, 0xAB, 0xCD, 0, 0 };
    const BYTE multiple[]{ 3, 0, 0xAB, 4, 0, 0xCD, 0xEF, 0, 0 };
    const BYTE shortItem[]{ 1, 0, 0, 0 };
    const BYTE tooLong[]{ 8, 0, 0xAB, 0xCD, 0, 0 };
    const BYTE unterminated[]{ 4, 0, 0xAB, 0xCD };
    const BYTE trailing[]{ 0, 0, 1, 0 };
    check(ValidPidl(root, sizeof(root)), "empty root PIDL");
    check(ValidPidl(item, sizeof(item)), "single item");
    check(ValidPidl(multiple, sizeof(multiple)), "multiple odd/even items");
    check(!ValidPidl(root, 0), "zero bytes");
    check(!ValidPidl(root, 1), "truncated terminator");
    check(!ValidPidl(shortItem, sizeof(shortItem)), "item shorter than its header");
    check(!ValidPidl(tooLong, sizeof(tooLong)), "item beyond supplied buffer");
    check(!ValidPidl(unterminated, sizeof(unterminated)), "missing terminator");
    check(!ValidPidl(trailing, sizeof(trailing)), "trailing bytes after terminator");
    std::vector<BYTE> maximum(kTargetFolderPidlCapacity, 0);
    maximum[0] = 0xFE; maximum[1] = 0xFF;
    check(ValidPidl(maximum.data(), maximum.size()), "64 KiB boundary");
    maximum.push_back(0);
    check(!ValidPidl(maximum.data(), maximum.size()), "over capacity");
    ULONGLONG low = 0, high = 0;
    check(ParseNonce(L"0123456789abcdefa123456789abcdef", low, high), "lowercase nonce");
    check(low == 0x0123456789ABCDEF && high == 0xA123456789ABCDEF, "nonce numeric values");
    check(!ParseNonce(L"00000000000000000000000000000000", low, high), "zero nonce");
    check(!ParseNonce(L"0123456789abcdeg0123456789abcdef", low, high), "nonhex nonce");
    check(!ParseNonce(L"0123456789abcdeF0123456789abcdef", low, high), "noncanonical nonce");
    const auto request = std::make_unique<TargetFolderRequest>();
    request->beforeCount = 2; request->beforeTabs[0] = 10; request->beforeTabs[1] = 20;
    Tabs tabs; tabs.count = 2; tabs.values[0] = 10; tabs.values[1] = 20;
    check(SameTabs(tabs, *request), "exact original tab set");
    tabs.values[1] = 30;
    check(!SameTabs(tabs, *request), "replaced original tab");
    tabs.values[1] = 20; tabs.count = 1;
    check(!SameTabs(tabs, *request), "missing original tab");
    tabs.count = 2; tabs.valid = false;
    check(!SameTabs(tabs, *request), "invalid enumeration");
    std::printf("Passed %u pure native protocol checks.\n", checks);
}
