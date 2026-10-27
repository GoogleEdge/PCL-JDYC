Imports System.IO
Imports System.Net.WebSockets
Imports System.Runtime.InteropServices
Imports System.Text
Imports Newtonsoft.Json.Linq

''' <summary>
''' 揭东一初联机大厅（hub.jdyc.top）客户端模块。
''' 登录复用 PCL 已有的皮肤站登录：把 ModJdyc 缓存的皮肤站 access token
''' （过期先由 JdycEnsureToken 刷新）发给大厅服务端 /api/oauth/skin-token 换大厅 JWT，
''' 不再弹第二次浏览器登录，不再查学生认证（皮肤站已认证；PCL 自身学生门禁仍在）。
''' 联网能力用 P/Invoke 调 jdyc-core.dll（frp / vlan / TAP），
''' frpc.exe、edge.exe、TAP 安装文件与 jdyc-core.dll 一起放在启动器 exe 同目录下，
''' 缺失时联网按钮不可用，页面会提示下载完整包。
''' </summary>
Public Module ModJdycHub

    '-------------------- 大厅地址与 JWT --------------------

    ''' <summary>大厅服务端地址，可在联机页修改，默认 hub.jdyc.top。</summary>
    Public Function HubBaseUrl() As String
        Dim Url = Settings.Get(Of String)("JdycHubBaseUrl")
        If String.IsNullOrWhiteSpace(Url) Then Url = "https://hub.jdyc.top"
        Return Url.TrimEnd("/"c)
    End Function

    ''' <summary>大厅 WS 地址：http→ws，https→wss，带 JWT 查询参数。</summary>
    Public Function HubWsUrl(Jwt As String) As String
        Dim Base = HubBaseUrl().Replace("https://", "wss://").Replace("http://", "ws://")
        Return Base & "/lobby?token=" & Jwt
    End Function

    ''' <summary>缓存的大厅 JWT，没有则现换一张。</summary>
    Public Function HubJwt() As String
        Dim Cached = Settings.Get(Of String)("CacheJdycHubJwt")
        If Not String.IsNullOrWhiteSpace(Cached) Then Return Cached
        Return HubExchangeJwt()
    End Function

    ''' <summary>用皮肤站 token 换大厅 JWT，失败返回空字符串。</summary>
    Public Function HubExchangeJwt() As String
        If Not JdycIsLoggedIn() Then Return ""
        If Not JdycEnsureToken() Then Return ""
        Dim SkinToken = Settings.Get(Of String)("CacheJdycAccess")
        If String.IsNullOrWhiteSpace(SkinToken) Then Return ""
        Try
            Dim Body = "{""skin_token"":""" & SkinToken & """}"
            Dim Resp = NetRequestByClient(HubBaseUrl() & "/api/oauth/skin-token", HttpMethod.Post,
                Content:=Body, ContentType:="application/json", RequireJson:=True)
            Dim Token = JObject.Parse(Resp).Value(Of String)("token")
            If String.IsNullOrWhiteSpace(Token) Then Return ""
            Settings.Set("CacheJdycHubJwt", Token)
            Return Token
        Catch ex As Exception
            Logger.Warn(ex, "换取大厅 JWT 失败")
            Return ""
        End Try
    End Function

    '-------------------- 大厅 REST（失败时清掉缓存 JWT 重换一次再试） --------------------

    Public Function HubApiGet(Path As String) As String
        For Attempt As Integer = 0 To 1
            Dim Jwt = HubJwt()
            If Jwt = "" Then Throw New Exception("大厅登录失败，请先连接大厅。")
            Try
                Return NetRequestByClient(HubBaseUrl() & Path, HttpMethod.Get,
                    Headers:={{"Authorization", "Bearer " & Jwt}}, RequireJson:=True)
            Catch ex As Exception
                If Attempt = 0 Then
                    Settings.Set("CacheJdycHubJwt", "")
                    Continue For
                End If
                Throw
            End Try
        Next
        Throw New Exception("大厅请求失败。")
    End Function

    Public Function HubApiPost(Path As String, Json As String) As String
        For Attempt As Integer = 0 To 1
            Dim Jwt = HubJwt()
            If Jwt = "" Then Throw New Exception("大厅登录失败，请先连接大厅。")
            Try
                Return NetRequestByClient(HubBaseUrl() & Path, HttpMethod.Post,
                    Content:=Json, ContentType:="application/json",
                    Headers:={{"Authorization", "Bearer " & Jwt}}, RequireJson:=True)
            Catch ex As Exception
                If Attempt = 0 Then
                    Settings.Set("CacheJdycHubJwt", "")
                    Continue For
                End If
                Throw
            End Try
        Next
        Throw New Exception("大厅请求失败。")
    End Function

    Public Function HubApiDelete(Path As String) As String
        Dim Jwt = HubJwt()
        If Jwt = "" Then Throw New Exception("大厅登录失败，请先连接大厅。")
        Return NetRequestByClient(HubBaseUrl() & Path, HttpMethod.Delete,
            Headers:={{"Authorization", "Bearer " & Jwt}})
    End Function

    '-------------------- jdyc-core.dll（P/Invoke，Cdecl，ANSI 字符串） --------------------

    <UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet:=CharSet.Ansi)>
    Public Delegate Sub JdycLogCallback(Level As Integer, <MarshalAs(UnmanagedType.LPStr)> Line As String)

    Private LogCallbackInstance As JdycLogCallback = AddressOf JdycLogHandler

    Private Sub JdycLogHandler(Level As Integer, Line As String)
        Select Case Level
            Case 1
                Logger.Warn("jdyc-core：" & Line)
            Case 2
                Logger.Error("jdyc-core：" & Line)
            Case Else
                Logger.Info("jdyc-core：" & Line)
        End Select
    End Sub

    <DllImport("jdyc-core.dll", CallingConvention:=CallingConvention.Cdecl, CharSet:=CharSet.Ansi, ExactSpelling:=True)>
    Private Function jdyc_frp_start(FrpcPath As String, TomlPath As String, Cb As JdycLogCallback) As Integer
    End Function

    <DllImport("jdyc-core.dll", CallingConvention:=CallingConvention.Cdecl, CharSet:=CharSet.Ansi, ExactSpelling:=True)>
    Private Function jdyc_frp_stop() As Integer
    End Function

    <DllImport("jdyc-core.dll", CallingConvention:=CallingConvention.Cdecl, CharSet:=CharSet.Ansi, ExactSpelling:=True)>
    Private Function jdyc_frp_running() As Integer
    End Function

    <DllImport("jdyc-core.dll", CallingConvention:=CallingConvention.Cdecl, CharSet:=CharSet.Ansi, ExactSpelling:=True)>
    Private Function jdyc_vlan_start(EdgePath As String, Community As String, Key As String, Supernode As String, MyIp As String, Cb As JdycLogCallback) As Integer
    End Function

    <DllImport("jdyc-core.dll", CallingConvention:=CallingConvention.Cdecl, CharSet:=CharSet.Ansi, ExactSpelling:=True)>
    Private Function jdyc_vlan_stop() As Integer
    End Function

    <DllImport("jdyc-core.dll", CallingConvention:=CallingConvention.Cdecl, CharSet:=CharSet.Ansi, ExactSpelling:=True)>
    Private Function jdyc_vlan_running() As Integer
    End Function

    <DllImport("jdyc-core.dll", CallingConvention:=CallingConvention.Cdecl, CharSet:=CharSet.Ansi, ExactSpelling:=True)>
    Private Function jdyc_tap_installed() As Integer
    End Function

    <DllImport("jdyc-core.dll", CallingConvention:=CallingConvention.Cdecl, CharSet:=CharSet.Ansi, ExactSpelling:=True)>
    Private Function jdyc_tap_install(TapDir As String) As Integer
    End Function

    '-------------------- 依赖文件与联网启停 --------------------

    Public Function HubAppDir() As String
        Return AppDomain.CurrentDomain.BaseDirectory
    End Function

    ''' <summary>缺失的联网组件文件名（exe 同目录下应有的文件）。</summary>
    Public Function HubMissingDeps() As List(Of String)
        Dim Missing As New List(Of String)
        For Each Name In {"jdyc-core.dll", "frpc.exe", "edge.exe", "tapinstall.exe", "OemVista.inf", "tap0901.cat", "tap0901.sys"}
            If Not File.Exists(Path.Combine(HubAppDir(), Name)) Then Missing.Add(Name)
        Next
        Return Missing
    End Function

    Public Function HubCoreAvailable() As Boolean
        Return File.Exists(Path.Combine(HubAppDir(), "jdyc-core.dll"))
    End Function

    Public Function HubNetRunning() As Boolean
        If Not HubCoreAvailable() Then Return False
        Try
            Return jdyc_frp_running() <> 0 OrElse jdyc_vlan_running() <> 0
        Catch ex As Exception
            Logger.Warn(ex, "查询联网状态失败")
            Return False
        End Try
    End Function

    ''' <summary>停止 frp 与 vlan（PCL 退出时调用，缺 dll 时直接返回）。</summary>
    Public Sub HubNetStop()
        If Not HubCoreAvailable() Then Return
        Try
            jdyc_frp_stop()
        Catch ex As Exception
            Logger.Warn(ex, "停止 frp 失败")
        End Try
        Try
            jdyc_vlan_stop()
        Catch ex As Exception
            Logger.Warn(ex, "停止 vlan 失败")
        End Try
    End Sub

    Public Function HubCoreCodeText(Code As Integer) As String
        Select Case Code
            Case 0
                Return "成功"
            Case -1
                Return "参数错误"
            Case -2
                Return "联网已在运行"
            Case -3
                Return "启动失败"
            Case -10
                Return "未安装 TAP 驱动"
            Case -11
                Return "TAP 驱动安装失败：请确认已以管理员身份运行 PCL；仍失败时，可在 exe 目录以管理员身份手动运行 tapinstall.exe install OemVista.inf tap0901 后重试"
            Case -12
                Return "需要管理员权限，请以管理员身份运行 PCL 后重试"
            Case Else
                Return "未知错误（" & Code & "）"
        End Select
    End Function

    ''' <summary>房主 frp 转发：写 frpc TOML 到临时文件，起 frpc。返回码见 HubCoreCodeText。</summary>
    Public Function HubFrpStart(FrpcServer As String, FrpcPort As Long, FrpcToken As String,
            ProxyName As String, RemotePort As Long, LocalPort As Long, PortType As String) As Integer
        If Not HubCoreAvailable() Then Return -3
        Dim FrpcExe = Path.Combine(HubAppDir(), "frpc.exe")
        If Not File.Exists(FrpcExe) Then Return -3
        Dim Toml As New StringBuilder()
        Toml.AppendLine("serverAddr = """ & FrpcServer & ":" & FrpcPort & """")
        If Not String.IsNullOrWhiteSpace(FrpcToken) Then Toml.AppendLine("auth.token = """ & FrpcToken & """")
        Toml.AppendLine("[[proxies]]")
        Toml.AppendLine("name = """ & ProxyName & """")
        Toml.AppendLine("type = """ & If(PortType = "udp", "udp", "tcp") & """")
        Toml.AppendLine("localPort = " & LocalPort)
        Toml.AppendLine("remotePort = " & RemotePort)
        Dim TomlPath = Path.Combine(Path.GetTempPath(), "jdyc-frp.toml")
        File.WriteAllText(TomlPath, Toml.ToString(), New UTF8Encoding(False))
        Try
            Return jdyc_frp_start(FrpcExe, TomlPath, LogCallbackInstance)
        Catch ex As Exception
            Logger.Warn(ex, "启动 frp 失败")
            Return -3
        End Try
    End Function

    ''' <summary>虚拟局域网：TAP 未装时先静默安装，再起 edge。返回码见 HubCoreCodeText。</summary>
    Public Function HubVlanStart(Community As String, Key As String, Supernode As String, MyIp As String) As Integer
        If Not HubCoreAvailable() Then Return -3
        Dim EdgeExe = Path.Combine(HubAppDir(), "edge.exe")
        If Not File.Exists(EdgeExe) Then Return -3
        Try
            If jdyc_tap_installed() <> 0 Then
                If jdyc_tap_install(HubAppDir()) <> 0 OrElse jdyc_tap_installed() <> 0 Then Return -11
            End If
            Return jdyc_vlan_start(EdgeExe, Community, Key, Supernode, MyIp, LogCallbackInstance)
        Catch ex As Exception
            Logger.Warn(ex, "启动 vlan 失败")
            Return -3
        End Try
    End Function

    ''' <summary>游戏默认端口（vlan 模式下拼游戏地址用，frp 模式用服务器分配的 remotePort，不走这里）。</summary>
    Public Function HubDefaultGamePort(Game As String) As Integer
        Select Case Game
            Case "terraria"
                Return 7777
            Case "cs16"
                Return 27015
            Case Else
                Return 25565
        End Select
    End Function

End Module
