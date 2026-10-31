/* core_internal.h：jdyc-core 内部共享定义（不对外发布）。 */
#ifndef JDYC_CORE_INTERNAL_H
#define JDYC_CORE_INTERNAL_H

#include <windows.h>
#include "jdyc_core.h"

typedef struct {
    HANDLE hProcess;
    HANDLE hThreadOut;
    HANDLE hThreadErr;
    HANDLE hPipeOutR, hPipeOutW;
    HANDLE hPipeErrR, hPipeErrW;
    jdyc_log_cb cb;
    int active;
} proc_state_t;

/* core.c */
void jdyc_lock_init(void);
proc_state_t* jdyc_frp_state(void);
proc_state_t* jdyc_edge_state(void);
wchar_t* jdyc_utf8_to_wide(const char* utf8);
void jdyc_log(proc_state_t* s, int level, const char* line);

/* process.c */
int jdyc_proc_start(proc_state_t* s, const char* exe_path, const char** args, int argc);
int jdyc_proc_stop(proc_state_t* s);
DWORD WINAPI jdyc_wait_thread(LPVOID param);

/* tap.c */
int jdyc_tap_check_registry(void);

#endif
