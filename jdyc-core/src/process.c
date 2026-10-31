/* process.c：jdyc_frp_* 与 jdyc_vlan_*，子进程启动/停止/管道日志。 */

#include <windows.h>
#include <stdio.h>
#include <string.h>
#include "jdyc_core.h"
#include "core_internal.h"

#define READ_BUF 4096

/* 管道读循环：逐行回调日志 */
static DWORD WINAPI read_loop(proc_state_t* s, HANDLE pipe, int level)
{
    char buf[READ_BUF];
    char line[READ_BUF];
    DWORD n = 0;
    DWORD got;
    size_t used = 0;
    while (ReadFile(pipe, buf, READ_BUF, &got, NULL) && got > 0) {
        for (n = 0; n < got; n++) {
            char c = buf[n];
            if (c == '\r') continue;
            if (c == '\n' || used >= READ_BUF - 1) {
                line[used] = 0;
                jdyc_log(s, level, line);
                used = 0;
            } else {
                line[used++] = c;
            }
        }
    }
    if (used > 0) {
        line[used] = 0;
        jdyc_log(s, level, line);
    }
    return 0;
}

static DWORD WINAPI read_out(LPVOID param)
{
    proc_state_t* s = (proc_state_t*)param;
    return read_loop(s, s->hPipeOutR, 0 /* INFO */);
}

static DWORD WINAPI read_err(LPVOID param)
{
    proc_state_t* s = (proc_state_t*)param;
    return read_loop(s, s->hPipeErrR, 1 /* WARN，stderr 行按 WARN 上报 */);
}

/* 启动监控线程 + 两个读线程 */
static void spawn_threads(proc_state_t* s)
{
    HANDLE w = CreateThread(NULL, 0, jdyc_wait_thread, s, 0, NULL);
    if (w) CloseHandle(w); /* 句柄不持有，线程自运行 */
    s->hThreadOut = CreateThread(NULL, 0, read_out, s, 0, NULL);
    s->hThreadErr = CreateThread(NULL, 0, read_err, s, 0, NULL);
}

int jdyc_frp_start(const char* frpc_path, const char* toml_path, jdyc_log_cb cb)
{
    proc_state_t* s = jdyc_frp_state();
    const char* args[2];
    int rc;
    if (!frpc_path || !toml_path) return -1;
    s->cb = cb;
    args[0] = "-c";
    args[1] = toml_path;
    rc = jdyc_proc_start(s, frpc_path, args, 2);
    if (rc == 0) {
        jdyc_log(s, 0, "frpc 子进程已启动");
        spawn_threads(s);
    }
    return rc;
}

int jdyc_frp_stop(void)
{
    return jdyc_proc_stop(jdyc_frp_state());
}

int jdyc_vlan_start(const char* edge_path, const char* community,
                    const char* key, const char* supernode,
                    const char* my_ip, jdyc_log_cb cb)
{
    proc_state_t* s = jdyc_edge_state();
    const char* args[10];
    int rc, i = 0;
    if (!edge_path || !community || !key || !supernode || !my_ip) return -1;
    s->cb = cb;
    args[i++] = "-c";     args[i++] = community;
    args[i++] = "-k";     args[i++] = key;
    args[i++] = "-l";     args[i++] = supernode;
    args[i++] = "-a";     args[i++] = my_ip;
    args[i++] = "-r";
    args[i++] = "-f";
    rc = jdyc_proc_start(s, edge_path, args, i);
    if (rc == 0) {
        jdyc_log(s, 0, "edge 子进程已启动");
        spawn_threads(s);
    }
    return rc;
}

int jdyc_vlan_stop(void)
{
    return jdyc_proc_stop(jdyc_edge_state());
}
