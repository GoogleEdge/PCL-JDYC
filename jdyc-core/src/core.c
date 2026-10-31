/* core.c：全局状态、日志回调、UTF-8 转宽字符等公共函数。 */

#include <windows.h>
#include <stdio.h>
#include <string.h>
#include "jdyc_core.h"
#include "core_internal.h"

/* 运行状态结构定义见 core_internal.h */

static proc_state_t g_frp;
static proc_state_t g_edge;
static CRITICAL_SECTION g_lock;
static int g_lockInit = 0;

void jdyc_lock_init(void)
{
    if (!g_lockInit) {
        InitializeCriticalSection(&g_lock);
        g_lockInit = 1;
    }
}

jdyc_log_cb jdyc_get_cb(proc_state_t* s) { return s ? s->cb : NULL; }

int jdyc_frp_running(void) { return g_frp.active ? 1 : 0; }
int jdyc_vlan_running(void) { return g_edge.active ? 1 : 0; }

proc_state_t* jdyc_frp_state(void) { return &g_frp; }
proc_state_t* jdyc_edge_state(void) { return &g_edge; }

/* UTF-8 → UTF-16，返回需 free 的堆内存 */
wchar_t* jdyc_utf8_to_wide(const char* utf8)
{
    int n;
    wchar_t* out;
    if (!utf8) return NULL;
    n = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, NULL, 0);
    if (n <= 0) return NULL;
    out = (wchar_t*)HeapAlloc(GetProcessHeap(), 0, n * sizeof(wchar_t));
    if (!out) return NULL;
    MultiByteToWideChar(CP_UTF8, 0, utf8, -1, out, n);
    return out;
}

/* 记录一行日志（线程安全，回调可空） */
void jdyc_log(proc_state_t* s, int level, const char* line)
{
    jdyc_log_cb cb;
    if (!line) return;
    cb = s ? s->cb : NULL;
    if (cb) cb(level, line);
}

/* 由参数数组构造 CreateProcessW 命令行（整体加引号，含空格的参数单独加引号） */
static wchar_t* build_cmdline(wchar_t* exe, wchar_t** argv, int argc)
{
    int i, len = (int)wcslen(exe) + 3;
    wchar_t* p;
    for (i = 0; i < argc; i++) len += (int)wcslen(argv[i]) + 3;
    p = (wchar_t*)HeapAlloc(GetProcessHeap(), 0, len * sizeof(wchar_t));
    if (!p) return NULL;
    wsprintfW(p, L"\"%s\"", exe);
    for (i = 0; i < argc; i++) {
        wchar_t* end = p + wcslen(p);
        wsprintfW(end, L" \"%s\"", argv[i]);
    }
    return p;
}

/* 公共启动函数：成功返回 0，参数空 -1，已在运行 -2，启动失败 -3 */
int jdyc_proc_start(proc_state_t* s, const char* exe_path, const char** args, int argc)
{
    wchar_t *wexe, *wcmd;
    wchar_t** wargs;
    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    SECURITY_ATTRIBUTES sa;
    int i, rc = -3;

    jdyc_lock_init();
    if (!exe_path) return -1;
    EnterCriticalSection(&g_lock);
    if (s->active) {
        LeaveCriticalSection(&g_lock);
        return -2;
    }
    memset(s, 0, sizeof(*s));

    /* 两根管道分别接 stdout/stderr */
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = TRUE;
    sa.lpSecurityDescriptor = NULL;
    if (!CreatePipe(&s->hPipeOutR, &s->hPipeOutW, &sa, 0) ||
        !CreatePipe(&s->hPipeErrR, &s->hPipeErrW, &sa, 0)) {
        LeaveCriticalSection(&g_lock);
        return -3;
    }

    wexe = jdyc_utf8_to_wide(exe_path);
    wargs = (wchar_t**)HeapAlloc(GetProcessHeap(), 0, sizeof(wchar_t*) * (argc > 0 ? argc : 1));
    if (!wexe || (!wargs && argc > 0)) goto cleanup;
    for (i = 0; i < argc; i++) {
        wargs[i] = jdyc_utf8_to_wide(args[i]);
        if (!wargs[i]) goto cleanup;
    }
    wcmd = build_cmdline(wexe, wargs, argc);
    if (!wcmd) goto cleanup;

    memset(&si, 0, sizeof(si));
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdInput = NULL;
    si.hStdOutput = s->hPipeOutW;
    si.hStdError = s->hPipeErrW;
    memset(&pi, 0, sizeof(pi));

    /* 参数数组语义：CreateProcessW 收拼好的命令行，但拼装由数组逐个完成，不做 shell 拼接 */
    if (!CreateProcessW(wexe, wcmd, NULL, NULL, TRUE,
                        CREATE_NO_WINDOW, NULL, NULL, &si, &pi)) {
        HeapFree(GetProcessHeap(), 0, wcmd);
        rc = -3;
        goto cleanup;
    }
    HeapFree(GetProcessHeap(), 0, wcmd);
    CloseHandle(pi.hThread);
    s->hProcess = pi.hProcess;
    s->active = 1;
    rc = 0;

cleanup:
    if (wexe) HeapFree(GetProcessHeap(), 0, wexe);
    if (wargs) {
        for (i = 0; i < argc; i++) if (wargs[i]) HeapFree(GetProcessHeap(), 0, wargs[i]);
        HeapFree(GetProcessHeap(), 0, wargs);
    }
    /* 父进程侧关掉写端，读端才能收到 EOF */
    if (rc == 0) {
        CloseHandle(s->hPipeOutW);
        CloseHandle(s->hPipeErrW);
        s->hPipeOutW = NULL;
        s->hPipeErrW = NULL;
    } else {
        if (s->hPipeOutR) CloseHandle(s->hPipeOutR);
        if (s->hPipeOutW) CloseHandle(s->hPipeOutW);
        if (s->hPipeErrR) CloseHandle(s->hPipeErrR);
        if (s->hPipeErrW) CloseHandle(s->hPipeErrW);
        memset(s, 0, sizeof(*s));
    }
    LeaveCriticalSection(&g_lock);
    return rc;
}

/* 公共停止函数：TerminateProcess 后回收句柄 */
int jdyc_proc_stop(proc_state_t* s)
{
    jdyc_lock_init();
    EnterCriticalSection(&g_lock);
    if (!s->active) {
        LeaveCriticalSection(&g_lock);
        return 0;
    }
    TerminateProcess(s->hProcess, 0);
    WaitForSingleObject(s->hProcess, 3000);
    CloseHandle(s->hProcess);
    s->hProcess = NULL;
    s->active = 0;
    if (s->hThreadOut) { WaitForSingleObject(s->hThreadOut, 2000); CloseHandle(s->hThreadOut); s->hThreadOut = NULL; }
    if (s->hThreadErr) { WaitForSingleObject(s->hThreadErr, 2000); CloseHandle(s->hThreadErr); s->hThreadErr = NULL; }
    if (s->hPipeOutR) CloseHandle(s->hPipeOutR);
    if (s->hPipeErrR) CloseHandle(s->hPipeErrR);
    s->hPipeOutR = NULL;
    s->hPipeErrR = NULL;
    LeaveCriticalSection(&g_lock);
    return 0;
}

/* 进程退出监控线程：子进程自己退出时清状态 */
DWORD WINAPI jdyc_wait_thread(LPVOID param)
{
    proc_state_t* s = (proc_state_t*)param;
    WaitForSingleObject(s->hProcess, INFINITE);
    EnterCriticalSection(&g_lock);
    s->active = 0;
    CloseHandle(s->hProcess);
    s->hProcess = NULL;
    LeaveCriticalSection(&g_lock);
    return 0;
}
