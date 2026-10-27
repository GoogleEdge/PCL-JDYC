Imports System.IO
Imports System.Net.WebSockets
Imports System.Text
Imports System.Threading
Imports System.Windows.Input
Imports Newtonsoft.Json.Linq

''' <summary>
''' 揭东一初联机大厅页（PCL 原生 UI：MyPageRight + MyCard + MyButton + MyListItem）。
''' 顶栏【联机】按钮点进来就是这一页，不再是 PCL 自带的老联机页。
''' 仅支持 Minecraft：不选游戏，端口默认 25565 可改，加入即自动联网。
''' 登录复用皮肤站登录态，大厅 JWT 经 /api/oauth/skin-token 换取；
''' 房间列表走 REST，加入房间走 WS（/lobby），联网走 jdyc-core.dll。
''' WS 连接在页面实例存活期间保持（房主断线后服务器会清扫房间），
''' 只在【断开】或 PCL 退出时关闭；页面切换不影响 WS。
''' </summary>
Public Class PageJdycHub

    Private SelectedRoomId As Long = -1
    Private SelectedRoom As JObject = Nothing
    Private LastRooms As New List(Of JObject)
    Private JoinedRoomId As Long = -1
    Private JoinedMode As String = ""
    Private JoinedIsHost As Boolean = False
    Private JoinedFrp As JObject = Nothing
    Private JoinedVlan As JObject = Nothing
    Private JoinedGameAddr As String = ""
    Private NetRunning As Boolean = False
    Private HubNickname As String = ""
    Private HubLocalId As Long = -1
    Private GameCode As String = "minecraft"
    Private ModeCode As String = "frp"
    Private LastCreateLocalPort As Integer = 25565
    Private Ws As ClientWebSocket = Nothing
    Private WsLoopRunning As Boolean = False
    Private Connecting As Boolean = False

    Public Sub New()
        InitializeComponent()
        TxtServer.Text = HubBaseUrl()
        RadioModeFrp.Checked = True
    End Sub

    Private Sub PageJdycHub_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        RefreshStatus()
        If Not Connecting Then
            Connecting = True
            Dim T As New Thread(AddressOf ConnectAndRefresh) With {.IsBackground = True}
            T.Start()
        End If
    End Sub

    '-------------------- 状态与房间列表 --------------------

    Private Sub RefreshStatus()
        Dim SkinName = Settings.Get(Of String)("CacheJdycName")
        Dim HubState = If(String.IsNullOrWhiteSpace(Settings.Get(Of String)("CacheJdycHubJwt")), "未连接", "已连接")
        If JdycIsLoggedIn() Then
            LabStatus.Text = "皮肤站已登录：" & SkinName & "，大厅：" & HubState & "。"
        Else
            LabStatus.Text = "皮肤站未登录，请先在启动页完成皮肤站登录，再点【连接大厅】。"
        End If
        '房间名默认：皮肤站用户名 + 的房间（只在输入框为空时填，不覆盖用户自己改的名字）。
        If JdycIsLoggedIn() AndAlso SkinName <> "" AndAlso String.IsNullOrWhiteSpace(TxtRoomName.Text) Then
            Dim Base = SkinName
            If Base.Length > 17 Then Base = Base.Substring(0, 17)
            TxtRoomName.Text = Base & "的房间"
        End If
        Dim Missing = HubMissingDeps()
        If Missing.Count > 0 Then
            HintDeps.Text = "缺少联网组件：" & String.Join("、", Missing) & "，联网功能不可用。请下载联机大厅完整包（zip，内含 exe 与上述文件放同一目录）后再用。"
            HintDeps.Visibility = Visibility.Visible
        Else
            HintDeps.Visibility = Visibility.Collapsed
        End If
    End Sub

    Private Sub ConnectAndRefresh()
        Try
            Connect(False)
        Finally
            Connecting = False
        End Try
    End Sub

    ''' <summary>连接大厅：换 JWT → 取身份 → 连 WS → 刷房间。后台线程调用。</summary>
    Private Sub Connect(Force As Boolean)
        Dim Jwt = If(Force, HubExchangeJwt(), HubJwt())
        If Jwt = "" Then
            RunInUi(Sub() LabStatus.Text = "大厅连接失败：请先完成皮肤站登录，或检查网络。")
            Return
        End If
        Try
            Dim MeJson = JObject.Parse(HubApiGet("/api/me"))
            HubNickname = If(MeJson("nickname") Is Nothing, "", MeJson("nickname").ToString())
            HubLocalId = If(MeJson("id") Is Nothing, -1, CLng(MeJson("id").ToString()))
        Catch ex As Exception
            Logger.Warn(ex, "获取大厅身份失败")
        End Try
        WsConnect(Jwt)
        RefreshRooms()
        RunInUi(AddressOf RefreshStatus)
    End Sub

    Private Sub RefreshRooms()
        Try
            Dim RoomsJson = JObject.Parse(HubApiGet("/api/rooms"))
            Dim List As New List(Of JObject)
            For Each R As JToken In CType(RoomsJson("rooms"), JArray)
                List.Add(CType(R, JObject))
            Next
            RunInUi(Sub() BuildRoomList(List))
        Catch ex As Exception
            Logger.Warn(ex, "加载房间列表失败")
            RunInUi(Sub() LabRoomsEmpty.Text = "房间列表加载失败，请点【刷新房间】重试。")
        End Try
    End Sub

    Private Sub BuildRoomList(Rooms As List(Of JObject))
        LastRooms = Rooms
        PanRooms.Children.Clear()
        LabRoomsEmpty.Visibility = If(Rooms.Count = 0, Visibility.Visible, Visibility.Collapsed)
        If Rooms.Count = 0 Then LabRoomsEmpty.Text = "暂无房间，点击【刷新房间】试试。"
        For Each R In Rooms
            Dim Item As New MyListItem With {
                .Title = R("name").ToString(),
                .Info = GameName(R("game").ToString()) & " · " & ModeName(R("mode").ToString()) &
                        " · 房主 " & R("host_nickname").ToString() &
                        " · " & R("members").ToString() & "/" & R("max_members").ToString(),
                .Tag = CLng(R("id").ToString()),
                .Margin = New System.Windows.Thickness(0, 0, 0, 6)
            }
            AddHandler Item.MouseLeftButtonUp, AddressOf RoomItem_Click
            PanRooms.Children.Add(Item)
        Next
    End Sub

    Private Function GameName(Code As String) As String
        Select Case Code
            Case "terraria"
                Return "泰拉瑞亚"
            Case "cs16"
                Return "CS1.6"
            Case "yuzu"
                Return "Yuzu"
            Case "custom"
                Return "其他"
            Case Else
                Return "Minecraft"
        End Select
    End Function

    Private Function ModeName(Code As String) As String
        Return If(Code = "vlan", "虚拟局域网", "端口映射")
    End Function

    Private Sub RoomItem_Click(sender As Object, e As MouseButtonEventArgs)
        Dim Id = CLng(DirectCast(sender, MyListItem).Tag)
        For Each R In LastRooms
            If CLng(R("id").ToString()) = Id Then
                SelectedRoomId = Id
                SelectedRoom = R
                Dim Info = "已选房间：" & R("name").ToString() & "（" & GameName(R("game").ToString()) &
                           "，" & ModeName(R("mode").ToString()) & "），房主 " & R("host_nickname").ToString() & "。"
                If R("mode").ToString() = "frp" AndAlso R("frp_addr") IsNot Nothing Then
                    Info &= "加入地址：" & R("frp_addr").ToString()
                End If
                LabNetInfo.Text = Info
                Return
            End If
        Next
    End Sub

    '-------------------- 按钮 --------------------

    Private Sub BtnConnect_Click(sender As Object, e As RoutedEventArgs) Handles BtnConnect.Click
        If Connecting Then Return
        Connecting = True
        Dim T As New Thread(Sub()
                                Try
                                    Connect(True)
                                Finally
                                    Connecting = False
                                End Try
                            End Sub) With {.IsBackground = True}
        T.Start()
    End Sub

    Private Sub BtnRefresh_Click(sender As Object, e As RoutedEventArgs) Handles BtnRefresh.Click
        Dim T As New Thread(AddressOf RefreshRooms) With {.IsBackground = True}
        T.Start()
    End Sub

    Private Sub BtnDisconnect_Click(sender As Object, e As RoutedEventArgs) Handles BtnDisconnect.Click
        WsClose()
        HubNetStop()
        NetRunning = False
        Settings.Set("CacheJdycHubJwt", "")
        LabNetInfo.Text = "已断开大厅连接，联网已停止。"
        RefreshStatus()
    End Sub

    Private Sub BtnSaveServer_Click(sender As Object, e As RoutedEventArgs) Handles BtnSaveServer.Click
        Dim Url = TxtServer.Text.Trim().TrimEnd("/"c)
        If Not (Url.StartsWith("http://") OrElse Url.StartsWith("https://")) Then
            Hint("服务器地址要以 http:// 或 https:// 开头。", HintType.Red)
            Return
        End If
        Settings.Set("JdycHubBaseUrl", Url)
        Settings.Set("CacheJdycHubJwt", "")
        Hint("已保存，点【连接大厅】重新连接。", HintType.Green)
    End Sub

    Private Sub BtnCreate_Click(sender As Object, e As RoutedEventArgs) Handles BtnCreate.Click
        Dim Name = TxtRoomName.Text.Trim()
        If Name.Length < 1 OrElse Name.Length > 20 Then
            Hint("房间名需为 1-20 字。", HintType.Red)
            Return
        End If
        Dim LocalPort As Integer = 0
        If Not Integer.TryParse(TxtLocalPort.Text.Trim(), LocalPort) OrElse LocalPort < 1 OrElse LocalPort > 65535 Then
            Hint("端口需为 1-65535。", HintType.Red)
            Return
        End If
        LastCreateLocalPort = LocalPort
        Dim Body = "{""name"":""" & JsonEscape(Name) & """,""game"":""" & GameCode &
                   """,""mode"":""" & ModeCode & """,""port_type"":""tcp"",""local_port"":" & LocalPort & "}"
        Dim T As New Thread(Sub() CreateRoom(Body)) With {.IsBackground = True}
        T.Start()
    End Sub

    Private Sub CreateRoom(Body As String)
        Try
            Dim Resp = JObject.Parse(HubApiPost("/api/rooms", Body))
            Dim Room = CType(Resp("room"), JObject)
            SelectedRoomId = CLng(Room("id").ToString())
            SelectedRoom = Room
            RunInUi(Sub()
                        LabNetInfo.Text = "房间已创建，正在加入并启动联网……"
                    End Sub)
            JoinSelectedRoom()
            RefreshRooms()
        Catch ex As Exception
            Logger.Warn(ex, "创建房间失败")
            RunInUi(Sub() Hint("创建房间失败：" & ex.Message, HintType.Red))
        End Try
    End Sub

    Private Sub BtnJoin_Click(sender As Object, e As RoutedEventArgs) Handles BtnJoin.Click
        If SelectedRoomId < 0 Then
            Hint("先在房间列表里点一个房间。", HintType.Red)
            Return
        End If
        Dim T As New Thread(AddressOf JoinSelectedRoom) With {.IsBackground = True}
        T.Start()
    End Sub

    Private Sub JoinSelectedRoom()
        Dim Jwt = HubJwt()
        If Jwt = "" Then
            RunInUi(Sub() Hint("请先点【连接大厅】。", HintType.Red))
            Return
        End If
        WsConnect(Jwt)
        If Ws Is Nothing OrElse Ws.State <> WebSocketState.Open Then
            RunInUi(Sub() Hint("大厅 WS 未连上，请点【连接大厅】重试。", HintType.Red))
            Return
        End If
        WsSend("{""type"":""join_room"",""room_id"":" & SelectedRoomId & "}")
        RunInUi(Sub() LabNetInfo.Text = "已发送加入请求，等待服务器回应…")
    End Sub



    Private Sub BtnCopyAddr_Click(sender As Object, e As RoutedEventArgs) Handles BtnCopyAddr.Click
        If String.IsNullOrWhiteSpace(JoinedGameAddr) Then
            Hint("还没有可复制的地址，先加入房间。", HintType.Red)
            Return
        End If
        System.Windows.Clipboard.SetText(JoinedGameAddr)
        Hint("已复制：" & JoinedGameAddr, HintType.Green)
    End Sub

    Private Sub BtnLeave_Click(sender As Object, e As RoutedEventArgs) Handles BtnLeave.Click
        If JoinedRoomId < 0 Then
            Hint("还没加入房间。", HintType.Red)
            Return
        End If
        Dim Id = JoinedRoomId
        Dim WasHost = JoinedIsHost
        Dim T As New Thread(Sub() LeaveRoom(Id, WasHost)) With {.IsBackground = True}
        T.Start()
    End Sub

    ''' <summary>离开房间：先调服务端（房主删房，访客发 leave_room），成功后再停本地联网并清状态。后台线程调用。</summary>
    Private Sub LeaveRoom(Id As Long, WasHost As Boolean)
        Try
            If WasHost Then
                HubApiDelete("/api/rooms/" & Id)
            Else
                WsSend("{""type"":""leave_room"",""room_id"":" & Id & "}")
            End If
        Catch ex As Exception
            Logger.Warn(ex, "离开房间失败")
            RunInUi(Sub() Hint("离开房间失败：" & ex.Message, HintType.Red))
            Return
        End Try
        HubNetStop()
        NetRunning = False
        JoinedRoomId = -1
        JoinedMode = ""
        JoinedIsHost = False
        JoinedFrp = Nothing
        JoinedVlan = Nothing
        JoinedGameAddr = ""
        RunInUi(Sub()
                    Hint(If(WasHost, "房间已关闭。", "已离开房间。"), HintType.Green)
                    LabNetInfo.Text = If(WasHost, "房间已关闭。", "已离开房间。")
                End Sub)
        RefreshRooms()
    End Sub

    Private Function JsonEscape(Text As String) As String
        Return Text.Replace("\", "\\").Replace("""", "\""")
    End Function

    '-------------------- WS（后台线程收发，UI 更新走 RunInUi） --------------------

    ''' <summary>连 WS（已连上则直接返回）。只在后台线程调用。</summary>
    Private Sub WsConnect(Jwt As String)
        If Ws IsNot Nothing AndAlso Ws.State = WebSocketState.Open Then Return
        Try
            Ws = New ClientWebSocket()
            Ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20)
            Ws.ConnectAsync(New Uri(HubWsUrl(Jwt)), Threading.CancellationToken.None).GetAwaiter().GetResult()
            WsLoopRunning = True
            Dim T As New Thread(AddressOf WsLoop) With {.IsBackground = True}
            T.Start()
        Catch ex As Exception
            Logger.Warn(ex, "大厅 WS 连接失败")
            Ws = Nothing
        End Try
    End Sub

    Private Sub WsSend(Json As String)
        If Ws Is Nothing OrElse Ws.State <> WebSocketState.Open Then Return
        Try
            Dim Bytes = Encoding.UTF8.GetBytes(Json)
            Ws.SendAsync(New ArraySegment(Of Byte)(Bytes), WebSocketMessageType.Text, True,
                Threading.CancellationToken.None).GetAwaiter().GetResult()
        Catch ex As Exception
            Logger.Warn(ex, "大厅 WS 发送失败")
        End Try
    End Sub

    ''' <summary>关闭 WS：直接 Abort，不等待握手（任何线程调用都安全；房主断线后服务器会清扫房间）。</summary>
    Private Sub WsClose()
        WsLoopRunning = False
        Try
            If Ws IsNot Nothing Then Ws.Abort()
        Catch ex As Exception
            Logger.Warn(ex, "大厅 WS 关闭失败")
        End Try
        Ws = Nothing
    End Sub

    Private Sub WsLoop()
        Dim Buffer(8191) As Byte
        While WsLoopRunning AndAlso Ws IsNot Nothing AndAlso Ws.State = WebSocketState.Open
            Try
                Dim Stream As New MemoryStream()
                Dim EndOfMessage As Boolean = False
                While Not EndOfMessage
                    Dim Result = Ws.ReceiveAsync(New ArraySegment(Of Byte)(Buffer),
                        Threading.CancellationToken.None).GetAwaiter().GetResult()
                    If Result.MessageType = WebSocketMessageType.Close Then
                        WsLoopRunning = False
                        Exit While
                    End If
                    Stream.Write(Buffer, 0, Result.Count)
                    EndOfMessage = Result.EndOfMessage
                End While
                If Not WsLoopRunning Then Exit While
                HandleWsMessage(Encoding.UTF8.GetString(Stream.ToArray()))
            Catch ex As Exception
                Logger.Warn(ex, "大厅 WS 接收失败")
                Exit While
            End Try
        End While
    End Sub

    Private Sub HandleWsMessage(Text As String)
        Dim Msg As JObject
        Try
            Msg = JObject.Parse(Text)
        Catch ex As Exception
            Logger.Warn(ex, "大厅 WS 消息解析失败")
            Return
        End Try
        Select Case Msg.Value(Of String)("type")
            Case "room_list"
                Try
                    Dim List As New List(Of JObject)
                    For Each R As JToken In CType(Msg("rooms"), JArray)
                        List.Add(CType(R, JObject))
                    Next
                    RunInUi(Sub() BuildRoomList(List))
                Catch ex As Exception
                    Logger.Warn(ex, "大厅房间列表解析失败")
                End Try
            Case "room_joined"
                ApplyJoined(Msg)
            Case "room_full"
                RunInUi(Sub() Hint("房间已满。", HintType.Red))
            Case "ping"
                WsSend("{""type"":""pong""}")
            Case "error"
                Dim ErrMsg = If(Msg("msg") Is Nothing, "大厅出错", Msg("msg").ToString())
                RunInUi(Sub() Hint("大厅：" & ErrMsg, HintType.Red))
        End Select
    End Sub

    Private Sub ApplyJoined(Msg As JObject)
        Dim RoomId = CLng(Msg("room_id").ToString())
        Dim Mode = Msg("mode").ToString()
        Dim HostUid = If(Msg("host_uid") Is Nothing, -1, CLng(Msg("host_uid").ToString()))
        Dim IsHost = (HubLocalId >= 0 AndAlso HostUid = HubLocalId)
        Dim Frp As JObject = Nothing
        Dim Vlan As JObject = Nothing
        If Mode = "frp" AndAlso Msg("frp") IsNot Nothing Then Frp = CType(Msg("frp"), JObject)
        If Mode = "vlan" AndAlso Msg("vlan") IsNot Nothing Then Vlan = CType(Msg("vlan"), JObject)
        Dim GameAddr As String = ""
        If Mode = "frp" Then
            If SelectedRoom IsNot Nothing AndAlso SelectedRoom("frp_addr") IsNot Nothing Then
                GameAddr = SelectedRoom("frp_addr").ToString()
            ElseIf Frp IsNot Nothing Then
                GameAddr = Frp("frpc_server").ToString() & ":" & Frp("remote_port").ToString()
            End If
        ElseIf Vlan IsNot Nothing Then
            '房主用创建时填的端口，访客用默认 25565（服务器目前不返回房间端口）。
            GameAddr = Vlan("host_virt_ip").ToString() & ":" & If(IsHost, LastCreateLocalPort, 25565)
        End If
        RunInUi(Sub()
                    JoinedRoomId = RoomId
                    JoinedMode = Mode
                    JoinedIsHost = IsHost
                    JoinedFrp = Frp
                    JoinedVlan = Vlan
                    JoinedGameAddr = GameAddr
                    Dim Info = "已加入房间：" & Msg("name").ToString() & "（" & ModeName(Mode) & "）。"
                    If Mode = "frp" Then
                        Info &= If(IsHost, "你是房主，正在启动转发，其他玩家连接：", "玩家连接地址：") & GameAddr
                    Else
                        Info &= "你的虚拟 IP：" & If(Vlan Is Nothing, "？", Vlan("my_virt_ip").ToString()) &
                                "，游戏地址：" & GameAddr & "。"
                    End If
                    LabNetInfo.Text = Info
                End Sub)
        '加入即自动启动联网（后台线程做，UI 更新回主线程）：
        '虚拟局域网房主和访客都要起 edge；端口映射只有房主起 frpc，访客直接复制地址进游戏。
        If Mode = "vlan" Then
            Dim T As New Thread(AddressOf StartVlanFromJoined) With {.IsBackground = True}
            T.Start()
        ElseIf IsHost AndAlso Mode = "frp" Then
            Dim T As New Thread(AddressOf StartFrpFromJoined) With {.IsBackground = True}
            T.Start()
        End If
    End Sub

    '-------------------- 联网启动（后台线程调用，返回后回主线程更新状态） --------------------

    Private Sub StartFrpFromJoined()
        If JoinedFrp Is Nothing Then
            RunInUi(Sub() Hint("没有拿到转发参数，请重新加入房间。", HintType.Red))
            Return
        End If
        Dim Frp = JoinedFrp
        Dim LocalPort As Long = LastCreateLocalPort
        If Frp("local_port") IsNot Nothing AndAlso CLng(Frp("local_port").ToString()) > 0 Then
            LocalPort = CLng(Frp("local_port").ToString())
        End If
        Dim Code = HubFrpStart(Frp("frpc_server").ToString(), CLng(Frp("frpc_port").ToString()),
            If(Frp("frpc_token") Is Nothing, "", Frp("frpc_token").ToString()),
            Frp("proxy_name").ToString(), CLng(Frp("remote_port").ToString()),
            LocalPort, If(Frp("port_type") Is Nothing, "tcp", Frp("port_type").ToString()))
        RunInUi(Sub()
                    If Code = 0 OrElse Code = -2 Then
                        NetRunning = True
                        LabNetInfo.Text = "转发已启动，其他玩家连接：" & JoinedGameAddr
                    Else
                        LabNetInfo.Text = "转发启动失败：" & HubCoreCodeText(Code)
                    End If
                End Sub)
    End Sub

    Private Sub StartVlanFromJoined()
        If JoinedVlan Is Nothing Then
            RunInUi(Sub() Hint("没有拿到虚拟局域网参数，请重新加入房间。", HintType.Red))
            Return
        End If
        Dim Vlan = JoinedVlan
        Dim Code = HubVlanStart(Vlan("network_community").ToString(), Vlan("vlan_key").ToString(),
            Vlan("supernode").ToString(), Vlan("my_virt_ip").ToString())
        RunInUi(Sub()
                    If Code = 0 OrElse Code = -2 Then
                        NetRunning = True
                        LabNetInfo.Text = "虚拟局域网已启动，你的虚拟 IP：" & Vlan("my_virt_ip").ToString() &
                                          "，游戏地址：" & JoinedGameAddr
                    Else
                        LabNetInfo.Text = "虚拟局域网启动失败：" & HubCoreCodeText(Code)
                    End If
                End Sub)
    End Sub

    '-------------------- 选择器 --------------------

    Private Sub RadioModeFrp_Check(sender As Object, e As RouteEventArgs) Handles RadioModeFrp.Check
        ModeCode = "frp"
        RadioModeVlan.Checked = False
    End Sub

    Private Sub RadioModeVlan_Check(sender As Object, e As RouteEventArgs) Handles RadioModeVlan.Check
        ModeCode = "vlan"
        RadioModeFrp.Checked = False
    End Sub

    '-------------------- 外部入口与退出清理 --------------------

    ''' <summary>切换到联机页（事件系统【加入房间】调用）。第一版暂不支持邀请码直连。</summary>
    Public Shared Sub Join()
        FrmMain.PageChange(FormMain.PageType.Link)
    End Sub

    Public Shared Sub Join(Code As String)
        FrmMain.PageChange(FormMain.PageType.Link)
        Hint("大厅第一版暂不支持邀请码直连，请在房间列表里找到房间加入。", HintType.Blue)
    End Sub

    ''' <summary>PCL 退出时调用：房主主持中则先问一句，随后停联网、断 WS，返回 False 表示允许退出。</summary>
    Public Function TryExit(Silent As Boolean, Closing As Boolean) As Boolean
        If Not Silent AndAlso JoinedIsHost AndAlso NetRunning Then
            If MyMsgBox("你正在主持联机房间，退出 PCL 会关闭房间，确定退出吗？", "退出联机", "确定", "取消") = 2 Then Return True
        End If
        HubNetStop()
        WsClose()
        Return False
    End Function

End Class
