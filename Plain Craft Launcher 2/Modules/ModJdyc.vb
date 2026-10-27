Imports System.Net
Imports System.Security.Cryptography
Imports System.Text
Imports Newtonsoft.Json.Linq

''' <summary>
''' 揭东一初皮肤站（Blessing Skin）登录模块。
''' 流程：浏览器 OAuth 授权 -> 本地 127.0.0.1:8765 收 code
''' -> JDYC 插件（服务器端）用 OAuth Secret 换 token -> 查学生认证
''' -> 通过后才允许进入启动器，登录方式记为“我校皮肤站登陆”。
''' OAuth Secret 绝不出现在这里，启动器只持有用户的 access/refresh token。
''' </summary>
Public Module ModJdyc

    Public Const JdycSite As String = "https://mcskin.jdyc.top"
    Public Const JdycApi As String = "https://mcskin.jdyc.top/api"
    Public Const JdycYggRoot As String = "https://mcskin.jdyc.top/api/yggdrasil"
    Public Const JdycOAuthClientId As Integer = 1
    Public Const JdycRedirectUri As String = "http://127.0.0.1:8765/oauth/callback"
    '注意：Blessing Skin 核心把 Closet 写权限 scope 注册成了拼写错误的 Closet.ReadWrtie（Wrtie），
    '请求正确拼写的 Closet.ReadWrite 会被 Passport 以 invalid_scope 拒绝，故此处必须用服务器实际注册的拼写。
    Public Const JdycScopes As String = "User.Read Player.Read Player.ReadWrite Closet.Read Closet.ReadWrtie"

    ''' <summary>离线登录是否被允许。默认关：只有在设置里打开后，离线登录才可用。</summary>
    Public Function JdycAllowOffline() As Boolean
        Return Settings.Get(Of Boolean)("JdycAllowOffline")
    End Function

    ''' <summary>是否已完成皮肤站登录（持有 token 且学生认证通过）。</summary>
    Public Function JdycIsLoggedIn() As Boolean
        Return Settings.Get(Of String)("CacheJdycAccess") <> "" AndAlso
               Settings.Get(Of Boolean)("CacheJdycStudentApproved")
    End Function

    Public Sub JdycLogout()
        Settings.Set("CacheJdycAccess", "")
        Settings.Set("CacheJdycRefresh", "")
        Settings.Set("CacheJdycExpires", 0)
        Settings.Set("CacheJdycName", "")
        Settings.Set("CacheJdycEmail", "")
        Settings.Set("CacheJdycUid", 0)
        Settings.Set("CacheJdycPlayerPid", 0)
        Settings.Set("CacheJdycPlayerName", "")
        Settings.Set("CacheJdycPlayerUuid", "")
        Settings.Set("CacheJdycStudentApproved", False)
        Settings.Set("CacheJdycYggAccess", "")
        Settings.Set("CacheJdycYggClientToken", "")
    End Sub

    ''' <summary>
    ''' 启动强制登录门：弹窗在 UI 线程弹（只有 UI 线程的弹窗才能真正挡住主窗体），
    ''' 登录的网络请求在后台线程跑（PCL 禁止在 UI 线程执行网络请求，见 ModNet）。
    ''' “登陆”走 OAuth（浏览器授权，本地收 code）；“注册”只开注册页，
    ''' 弹窗随即重新弹出，直到登录并通过学生认证为止。关闭主窗口即退出程序。
    ''' 在 FormMain 初始化的 UI 线程里调用，放在 EULA 之前。
    ''' </summary>
    Public Sub JdycGate()
        Dim LoginTask As Task(Of Boolean) = Nothing
        Do While Not JdycIsLoggedIn()
            If LoginTask IsNot Nothing AndAlso LoginTask.IsCompleted Then
                '后台登录已出结果：在 UI 线程收尾（成功进主界面，失败弹原因后重弹）。
                'GetAwaiter().GetResult() 会原样抛出后台的异常，不包 AggregateException。
                Try
                    If LoginTask.GetAwaiter().GetResult() Then
                        Settings.Set("LoginType", McLoginType.Jdyc)
                        Return
                    End If
                Catch ex As Exception
                    Logger.Warn(ex, "皮肤站登录失败")
                    MyMsgBox("皮肤站登录失败：" & ex.Message & vbCrLf & vbCrLf & "请确认已在皮肤站完成注册和学生认证，然后重试。", "登录失败", "我知道了", IsWarn:=True)
                End Try
                LoginTask = Nothing
                Continue Do
            End If
            If LoginTask IsNot Nothing Then
                '登录进行中：只弹等待框挡住主窗体，不新开登录任务。
                '浏览器里完成授权后，回到这里点我已完成授权，循环就会收后台任务的结果。
                MyMsgBox("正在等待浏览器完成皮肤站登录…" & vbCrLf & vbCrLf & "请在浏览器里完成授权，然后回到启动器点我已完成授权。", "正在登录", "我已完成授权", ForceWait:=True)
                Continue Do
            End If
            Dim Result = MyMsgBox(
                "欢迎使用揭东一初 PCL 启动器。" & vbCrLf & vbCrLf &
                "使用本启动器需要先登录我校皮肤站（mcskin.jdyc.top），" &
                "并完成学生认证。登录后启动器会自动配好我校皮肤站登陆，" &
                "不用再像 authlib 那样手动填服务器地址和账号。" & vbCrLf & vbCrLf &
                "正版登录不受本弹窗影响，登录后仍可在启动页选择正版；" &
                "离线登录默认不可用，需要在设置里打开后才能使用。",
                "揭东一初皮肤站登录",
                "我已注册并完成学生认证，登陆", "我要注册")
            If Result = 2 Then
                OpenWebsite(JdycSite & "/auth/register")
                Continue Do
            End If
            '只有点“登陆”（Result = 1）才进 OAuth；其他返回值一律重弹，绝不能跳过登录门。
            '登录全程在后台线程跑，UI 线程只弹弹窗：JdycLogin 本身要求非 UI 线程调用。
            If Result <> 1 Then
                Continue Do
            End If
            LoginTask = Task.Run(Function() JdycLogin())
        Loop
    End Sub

    ''' <summary>皮肤站头像地址（按 uid 取头像，站内 /avatar/user/{uid}）。</summary>
    Public Function JdycAvatarUrl() As String
        Return JdycSite & "/avatar/user/" & Settings.Get(Of Integer)("CacheJdycUid")
    End Function

    '-------------------- PKCE --------------------

    Private Function JdycRandomBase64Url(ByteCount As Integer) As String
        Dim Bytes(ByteCount - 1) As Byte
        Using Rng As New RNGCryptoServiceProvider()
            Rng.GetBytes(Bytes)
        End Using
        Return Convert.ToBase64String(Bytes).Replace("+", "-").Replace("/", "_").TrimEnd("="c)
    End Function

    Private Function JdycPkceChallenge(Verifier As String) As String
        Using Sha As SHA256 = SHA256.Create()
            Dim Hash = Sha.ComputeHash(Encoding.ASCII.GetBytes(Verifier))
            Return Convert.ToBase64String(Hash).Replace("+", "-").Replace("/", "_").TrimEnd("="c)
        End Using
    End Function

    '-------------------- 本地回调监听 --------------------

    ''' <summary>
    ''' 在 127.0.0.1:8765 等待浏览器把 code 送回来。
    ''' 返回 code，超时或 state 不符返回 Nothing。
    ''' </summary>
    Private Function JdycWaitForCode(ExpectedState As String, TimeoutSeconds As Integer) As String
        Dim Listener As New HttpListener()
        Listener.Prefixes.Add("http://127.0.0.1:8765/oauth/callback/")
        Try
            Listener.Start()
        Catch ex As Exception
            Throw New Exception("无法在本机 8765 端口监听 OAuth 回调，请检查是否有其他程序占用了该端口。", ex)
        End Try
        Try
            Dim AsyncResult = Listener.BeginGetContext(Nothing, Nothing)
            If Not AsyncResult.AsyncWaitHandle.WaitOne(TimeoutSeconds * 1000) Then
                Return Nothing
            End If
            Dim Context = Listener.EndGetContext(AsyncResult)
            Dim Query = Context.Request.QueryString
            Dim Code = Query("code")
            Dim State = Query("state")
            Dim ErrorText = Query("error")
            Dim Html As String
            If ErrorText <> "" Then
                Html = "<html><body><h2>登录未完成：" & ErrorText & "</h2><p>请回到启动器重试。</p></body></html>"
            ElseIf Code = "" OrElse State <> ExpectedState Then
                Html = "<html><body><h2>登录失败：回调校验未通过。</h2><p>请回到启动器重试。</p></body></html>"
                Code = Nothing
            Else
                Html = "<html><body><h2>登录成功！</h2><p>请回到启动器继续使用。</p></body></html>"
            End If
            Dim Bytes = Encoding.UTF8.GetBytes(Html)
            Context.Response.ContentType = "text/html; charset=utf-8"
            Context.Response.ContentLength64 = Bytes.Length
            Context.Response.OutputStream.Write(Bytes, 0, Bytes.Length)
            Context.Response.OutputStream.Close()
            Return Code
        Finally
            Listener.Stop()
            Listener.Close()
        End Try
    End Function

    '-------------------- OAuth 主流程 --------------------

    ''' <summary>
    ''' 完整走一次皮肤站 OAuth 登录：开浏览器、收 code、换 token、
    ''' 查用户信息和学生认证。通过返回 True，否则抛错或返回 False。
    ''' 必须在非 UI 线程调用（登录加载线程）。
    ''' </summary>
    Public Function JdycLogin() As Boolean
        Dim Verifier = JdycRandomBase64Url(64)
        Dim Challenge = JdycPkceChallenge(Verifier)
        Dim State = Guid.NewGuid().ToString("N")
        Dim AuthorizeUrl = JdycSite & "/oauth/authorize?client_id=" & JdycOAuthClientId &
            "&redirect_uri=" & Uri.EscapeDataString(JdycRedirectUri) &
            "&response_type=code&scope=" & Uri.EscapeDataString(JdycScopes) &
            "&state=" & State & "&code_challenge=" & Challenge & "&code_challenge_method=S256"
        '先起监听再开浏览器，避免浏览器回得比监听早。
        Dim CodeTask = Task.Run(Function() JdycWaitForCode(State, 300))
        OpenWebsite(AuthorizeUrl)
        Dim Code = CodeTask.Result
        If String.IsNullOrEmpty(Code) Then
            Throw New Exception("未收到皮肤站的登录回调（超时或被拒绝），请重试。")
        End If
        Dim TokenJson = JdycExchangeToken("authorization_code", Code, Verifier)
        JdycSaveToken(TokenJson)
        '用户信息
        Dim UserJson = JdycApiGet("/user").DeserializeJson()
        Settings.Set("CacheJdycUid", UserJson.Value(Of Integer)("uid"))
        Settings.Set("CacheJdycName", UserJson.Value(Of String)("nickname"))
        Settings.Set("CacheJdycEmail", UserJson.Value(Of String)("email"))
        '学生认证：未通过就清掉登录态，弹窗保持显示。
        Dim Student = JdycStudentStatus()
        If Not Student.Value(Of Boolean)("approved") Then
            Dim StatusText = Student.Value(Of String)("status")
            Dim Note = If(Student("review_note") Is Nothing OrElse Student("review_note").Type = JTokenType.Null, "", Student.Value(Of String)("review_note"))
            JdycLogout()
            Select Case StatusText
                Case "pending"
                    Throw New Exception("学生认证还在审核中，审核通过后才能登录启动器。")
                Case "rejected"
                    Throw New Exception("学生认证被退回" & If(Note = "", "。", "：" & Note) & "请到皮肤站重新提交认证。")
                Case "none"
                    Throw New Exception("你还没有提交学生认证，请先到皮肤站完成注册和学生认证。")
                Case "not_installed"
                    Throw New Exception("皮肤站的学生认证插件未正确安装，请联系站长检查。")
                Case Else
                    Throw New Exception("学生认证未通过，无法登录启动器。")
            End Select
        End If
        Settings.Set("CacheJdycStudentApproved", True)
        Return True
    End Function

    Private Sub JdycSaveToken(TokenJson As JObject)
        Settings.Set("CacheJdycAccess", TokenJson.Value(Of String)("access_token"))
        Settings.Set("CacheJdycRefresh", TokenJson.Value(Of String)("refresh_token"))
        Dim ExpiresIn As Long = 0
        Long.TryParse(TokenJson.Value(Of String)("expires_in"), ExpiresIn)
        Settings.Set("CacheJdycExpires", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ExpiresIn)
    End Sub

    ''' <summary>通过 JDYC 插件换 token（Secret 在服务器端，不经过启动器）。</summary>
    Private Function JdycExchangeToken(GrantType As String, CodeOrRefresh As String, Optional Verifier As String = Nothing) As JObject
        Dim Form As String
        If GrantType = "authorization_code" Then
            Form = "grant_type=authorization_code&client_id=" & JdycOAuthClientId &
                "&code=" & Uri.EscapeDataString(CodeOrRefresh) &
                "&redirect_uri=" & Uri.EscapeDataString(JdycRedirectUri) &
                "&code_verifier=" & Uri.EscapeDataString(Verifier)
        Else
            Form = "grant_type=refresh_token&client_id=" & JdycOAuthClientId &
                "&refresh_token=" & Uri.EscapeDataString(CodeOrRefresh)
        End If
        Dim Result = NetRequestByClient(JdycApi & "/jdyc/oauth/token", HttpMethod.Post,
            Content:=Form, ContentType:="application/x-www-form-urlencoded", RequireJson:=True)
        Return Result.DeserializeJson()
    End Function

    ''' <summary>token 快过期时用 refresh_token 换新 token，换不到返回 False。</summary>
    Public Function JdycEnsureToken() As Boolean
        Dim Access = Settings.Get(Of String)("CacheJdycAccess")
        If Access = "" Then Return False
        Dim Expires = Settings.Get(Of Long)("CacheJdycExpires")
        If Expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60 Then Return True
        Dim Refresh = Settings.Get(Of String)("CacheJdycRefresh")
        If Refresh = "" Then Return False
        Try
            Dim TokenJson = JdycExchangeToken("refresh_token", Refresh)
            JdycSaveToken(TokenJson)
            Return True
        Catch ex As Exception
            Logger.Warn(ex, "皮肤站 token 刷新失败")
            Return False
        End Try
    End Function

    '-------------------- 皮肤站 API --------------------

    Private Function JdycApiGet(Path As String) As String
        If Not JdycEnsureToken() Then Throw New Exception("皮肤站登录已过期，请重新登录。")
        Return NetRequestByClient(JdycApi & Path, HttpMethod.Get,
            Headers:={{"Authorization", "Bearer " & Settings.Get(Of String)("CacheJdycAccess")}}, RequireJson:=True)
    End Function

    Private Function JdycApiPost(Path As String, Form As String) As String
        If Not JdycEnsureToken() Then Throw New Exception("皮肤站登录已过期，请重新登录。")
        Return NetRequestByClient(JdycApi & Path, HttpMethod.Post, Content:=Form,
            ContentType:="application/x-www-form-urlencoded",
            Headers:={{"Authorization", "Bearer " & Settings.Get(Of String)("CacheJdycAccess")}}, RequireJson:=True)
    End Function

    Public Function JdycStudentStatus() As JObject
        Return JdycApiGet("/jdyc/student-status").DeserializeJson()
    End Function

    ''' <summary>当前用户的角色列表（/api/players 返回的数组，含 pid、name、tid_skin、tid_cape）。</summary>
    Public Function JdycPlayers() As JArray
        Return JdycApiGet("/players").DeserializeJson()
    End Function

    ''' <summary>当前用户的衣柜列表（category = skin 或 cape，返回分页对象的 data 数组）。</summary>
    Public Function JdycCloset(Category As String) As JArray
        Dim Result = JdycApiGet("/closet?category=" & Category & "&perPage=100").DeserializeJson()
        Return CType(Result("data"), JArray)
    End Function

    ''' <summary>
    ''' 上传 PNG 材质到皮肤站（走 JDYC 插件），返回新材质 tid。
    ''' 必须在非 UI 线程调用。type = steve / alex / cape，public 固定为 False（仅自己用）。
    ''' </summary>
    Public Function JdycUploadTexture(FilePath As String, TextureType As String, TextureName As String) As Integer
        If Not JdycEnsureToken() Then Throw New Exception("皮肤站登录已过期，请重新登录。")
        Dim Content As New Net.Http.MultipartFormDataContent From {
            {New Net.Http.StringContent(TextureName), "name"},
            {New Net.Http.StringContent(TextureType), "type"},
            {New Net.Http.StringContent("0"), "public"},
            {New Net.Http.ByteArrayContent(FileUtils.ReadAsBytes(FilePath)), "file", PathUtils.GetLastPart(FilePath)}
        }
        Dim Result = NetRequestByClient(JdycApi & "/jdyc/upload-texture", HttpMethod.Post, Content:=Content,
            Headers:={{"Authorization", "Bearer " & Settings.Get(Of String)("CacheJdycAccess")}}, RequireJson:=True)
        Dim Json = Result.DeserializeJson()
        Return Json.Value(Of Integer)("tid")
    End Function

    ''' <summary>把角色的皮肤/披风换成衣柜里的材质 tid（0 表示清除）。</summary>
    Public Sub JdycSetPlayerTexture(PlayerPid As Integer, SkinTid As Integer, CapeTid As Integer)
        Dim Form = "skin=" & SkinTid & "&cape=" & CapeTid
        JdycApiPost("/players/" & PlayerPid & "/textures", Form)
    End Sub

    ''' <summary>
    ''' 为当前选定角色签发 Yggdrasil 启动会话（走 JDYC 插件，不用密码）。
    ''' 返回 accessToken、clientToken、selectedProfile，PCL 按 Auth 方式启动。
    ''' </summary>
    Public Function JdycYggdrasilSession(PlayerPid As Integer) As JObject
        Dim Form = "player_pid=" & PlayerPid &
            "&clientToken=" & Uri.EscapeDataString(Settings.Get(Of String)("CacheJdycYggClientToken"))
        Dim Result = JdycApiPost("/jdyc/yggdrasil-session", Form).DeserializeJson()
        Settings.Set("CacheJdycYggAccess", Result.Value(Of String)("accessToken"))
        Settings.Set("CacheJdycYggClientToken", Result.Value(Of String)("clientToken"))
        If Result("selectedProfile") IsNot Nothing AndAlso Result("selectedProfile").Type <> JTokenType.Null AndAlso Result("selectedProfile")("id") IsNot Nothing Then
            Settings.Set("CacheJdycPlayerUuid", Result("selectedProfile").Value(Of String)("id"))
            Settings.Set("CacheJdycPlayerName", Result("selectedProfile").Value(Of String)("name"))
        End If
        Return Result
    End Function

End Module
