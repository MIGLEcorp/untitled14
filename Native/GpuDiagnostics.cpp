// Optional Windows DXGI helper for MBrowser. No changes to Chromium memory ownership.
// Export returns hardware adapter LOCAL video-memory budget/usage, not per-browser usage.
#include <windows.h>
#include <dxgi1_4.h>
#include <stdint.h>
#include <pdh.h>
#include <pdhmsg.h>
#include <vector>

extern "C" __declspec(dllexport) int __cdecl MB_QueryLocalVideoMemory(
    uint64_t* currentUsage, uint64_t* budget, uint64_t* availableForReservation)
{
    if (!currentUsage || !budget || !availableForReservation) return 0;
    *currentUsage = *budget = *availableForReservation = 0;
    IDXGIFactory1* factory = nullptr;
    if (FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void**)&factory))) return 0;
    int success = 0;
    for (UINT index = 0; ; ++index) {
        IDXGIAdapter1* adapter = nullptr;
        HRESULT enumResult = factory->EnumAdapters1(index, &adapter);
        if (enumResult == DXGI_ERROR_NOT_FOUND) break;
        if (FAILED(enumResult)) break;
        DXGI_ADAPTER_DESC1 desc = {};
        if (SUCCEEDED(adapter->GetDesc1(&desc)) && !(desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE)) {
            IDXGIAdapter3* adapter3 = nullptr;
            if (SUCCEEDED(adapter->QueryInterface(__uuidof(IDXGIAdapter3), (void**)&adapter3))) {
                DXGI_QUERY_VIDEO_MEMORY_INFO info = {};
                if (SUCCEEDED(adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &info))) {
                    *currentUsage = info.CurrentUsage;
                    *budget = info.Budget;
                    *availableForReservation = info.AvailableForReservation;
                    success = 1;
                }
                adapter3->Release();
            }
        }
        adapter->Release();
        if (success) break;
    }
    factory->Release();
    return success;
}

extern "C" __declspec(dllexport) int __cdecl MB_QuerySystemDedicatedVideoMemory(uint64_t* dedicatedUsage)
{
    if (!dedicatedUsage) return 0;
    *dedicatedUsage = 0;
    PDH_HQUERY query = nullptr;
    if (PdhOpenQueryW(nullptr, 0, &query) != ERROR_SUCCESS) return 0;
    int result = 0;
    PDH_HCOUNTER counter = nullptr;
    if (PdhAddEnglishCounterW(query, L"\\GPU Adapter Memory(*)\\Dedicated Usage", 0, &counter) == ERROR_SUCCESS &&
        PdhCollectQueryData(query) == ERROR_SUCCESS)
    {
        DWORD size = 0, count = 0;
        if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_LARGE, &size, &count, nullptr) == PDH_MORE_DATA &&
            size > 0 && size < 16 * 1024 * 1024)
        {
            std::vector<unsigned char> storage(size);
            auto* items = reinterpret_cast<PDH_FMT_COUNTERVALUE_ITEM_W*>(storage.data());
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_LARGE, &size, &count, items) == ERROR_SUCCESS)
            {
                bool valid = false;
                for (DWORD i = 0; i < count; ++i)
                {
                    if ((items[i].FmtValue.CStatus == PDH_CSTATUS_VALID_DATA ||
                         items[i].FmtValue.CStatus == PDH_CSTATUS_NEW_DATA) &&
                        items[i].FmtValue.largeValue >= 0)
                    {
                        *dedicatedUsage += static_cast<uint64_t>(items[i].FmtValue.largeValue);
                        valid = true;
                    }
                }
                result = valid ? 1 : 0;
            }
        }
    }
    PdhCloseQuery(query);
    return result;
}
