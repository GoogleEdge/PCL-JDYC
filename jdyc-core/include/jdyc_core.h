#ifndef JDYC_CORE_H
#define JDYC_CORE_H
#ifdef __cplusplus
extern "C" {
#endif
typedef void (*jdyc_log_cb)(int level, const char* line); /* 0=INFO 1=WARN 2=ERROR */
/* 返回码：0成功 -1参数空 -2已在运行 -3启动失败
   -10未装TAP -11 TAP安装失败 -12需管理员 */
int jdyc_frp_start(const char* frpc_path, const char* toml_path, jdyc_log_cb cb);
int jdyc_frp_stop(void);
int jdyc_vlan_start(const char* edge_path, const char* community,
                    const char* key, const char* supernode,
                    const char* my_ip, jdyc_log_cb cb);
int jdyc_vlan_stop(void);
int jdyc_tap_installed(void);                    /* 0已装 -10未装 */
int jdyc_tap_install(const char* tap_dir);       /* 用目录内 tapinstall.exe 静默装 tap0901 */
int jdyc_frp_running(void);
int jdyc_vlan_running(void);
#ifdef __cplusplus
}
#endif
#endif
