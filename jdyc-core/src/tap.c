/* tap.c：TAP-Windows6 (tap0901) 检测与静默安装。 */

#include <windows.h>
#include <stdio.h>
#include <string.h>
#include "jdyc_core.h"
#include "core_internal.h"

/* TAP 设备类 GUID（网卡类） */
static const WCHAR *NET_CLASS = L"SYSTEM\\CurrentControlSet\\Control\\Class\\{4D36E972-E325-11CE-BFC1-08002BE10318}";

/* 枚举网卡类下所有子键，找 ComponentId 以 "tap0901" 开头的设备 */
int jdyc_tap_check_registry(void)
{
    HKEY hKey;
    DWORD idx = 0;
    WCHAR name[256];
    DWORD nameLen = 256;
    LONG rc;

    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, NET_CLASS, 0, KEY_READ, &hKey) != ERROR_SUCCESS)
        return -10;
    for (;;) {
        nameLen = 256;
        rc = RegEnumKeyExW(hKey, idx++, name, &nameLen, NULL, NULL, NULL, NULL);
        if (rc != ERROR_SUCCESS) break;
        {
            HKEY sub;
            WCHAR compId[256];
            DWORD sz = sizeof(compId);
            WCHAR path[512];
            lstrcpyW(path, NET_CLASS);
            lstrcatW(path, L"\\");
            lstrcatW(path, name);
            if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, path, 0, KEY_READ, &sub) == ERROR_SUCCESS) {
                DWORD type;
                if (RegQueryValueExW(sub, L"ComponentId", NULL, &type,
                                     (LPBYTE)compId, &sz) == ERROR_SUCCESS) {
                    compId[255] = 0;
                    /* tap0901 大小写不敏感前缀匹配 */
                    WCHAR lower[256];
                    DWORD i;
                    for (i = 0; i < 256 && compId[i]; i++) {
                        WCHAR c = compId[i];
                        lower[i] = (c >= L'A' && c <= L'Z') ? (c + 32) : c;
                    }
                    lower[i] = 0;
                    if (wcsncmp(lower, L"tap0901", 7) == 0) {
                        RegCloseKey(sub);
                        RegCloseKey(hKey);
                        return 0;
                    }
                }
                RegCloseKey(sub);
            }
        }
    }
    RegCloseKey(hKey);
    return -10;
}

int jdyc_tap_installed(void)
{
    return jdyc_tap_check_registry();
}

/* 静默安装：用 tapinstall.exe（devcon 克隆）装驱动。
 * tap_dir 为驱动文件目录（tapinstall.exe / OemVista.inf / tap0901.cat / tap0901.sys）。
 * 客户端已以管理员运行，tapinstall 装驱动无需再提权。 */
int jdyc_tap_install(const char* tap_dir)
{
    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    wchar_t* dir;
    wchar_t* cmd;
    DWORD exitCode = 1;
    size_t len;

    if (!tap_dir) return -1;
    if (jdyc_tap_check_registry() == 0) return 0; /* 已装无需再装 */

    dir = jdyc_utf8_to_wide(tap_dir);
    if (!dir) return -1;
    len = lstrlenW(dir) + 96;
    cmd = (wchar_t*)HeapAlloc(GetProcessHeap(), 0, len * sizeof(WCHAR));
    if (!cmd) { HeapFree(GetProcessHeap(), 0, dir); return -1; }
    _snwprintf(cmd, len, L"\"%s\\tapinstall.exe\" install \"%s\\OemVista.inf\" tap0901",
               dir, dir);

    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    ZeroMemory(&pi, sizeof(pi));
    if (!CreateProcessW(NULL, cmd, NULL, NULL, FALSE,
                        CREATE_NO_WINDOW, NULL, NULL, &si, &pi)) {
        HeapFree(GetProcessHeap(), 0, dir);
        HeapFree(GetProcessHeap(), 0, cmd);
        return -11;
    }
    WaitForSingleObject(pi.hProcess, 120000); /* 最多等 2 分钟 */
    if (GetExitCodeProcess(pi.hProcess, &exitCode) && exitCode != 0) {
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
        HeapFree(GetProcessHeap(), 0, dir);
        HeapFree(GetProcessHeap(), 0, cmd);
        if (jdyc_tap_check_registry() == 0) return 0;
        return -11;
    }
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);
    HeapFree(GetProcessHeap(), 0, dir);
    HeapFree(GetProcessHeap(), 0, cmd);
    return jdyc_tap_check_registry();
}
