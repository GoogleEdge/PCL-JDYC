Public Class PageLoginJdycSkin

    Public Sub New()
        InitializeComponent()
        Skin.Loader = PageLaunchLeft.SkinJdyc
    End Sub
    Private Sub PageLoginJdycSkin_Loaded() Handles Me.Loaded
        Skin.Loader.Start()
    End Sub

    ''' <summary>
    ''' 刷新页面显示的所有信息。
    ''' </summary>
    Public Sub Reload(KeepInput As Boolean)
        TextName.Text = If(Settings.Get(Of String)("CacheJdycPlayerName") <> "", Settings.Get(Of String)("CacheJdycPlayerName"), Settings.Get(Of String)("CacheJdycName"))
        TextEmail.Text = Settings.Get(Of String)("CacheJdycEmail")
        TextEmail.Visibility = If(Settings.Get(Of Boolean)("UiLauncherEmail"), Visibility.Collapsed, Visibility.Visible)
        PageLoginJdycSkin_Loaded()
    End Sub
    ''' <summary>
    ''' 获取当前页面的登录信息（皮肤站登录不需要账号密码，见 McLoginJdyc）。
    ''' </summary>
    Public Shared Function GetLoginData() As McLoginJdyc
        Return New McLoginJdyc With {
            .PlayerPid = Settings.Get(Of Integer)("CacheJdycPlayerPid"),
            .PlayerName = Settings.Get(Of String)("CacheJdycPlayerName")}
    End Function

    Private Sub PageLoginJdycSkin_MouseEnter(sender As Object, e As MouseEventArgs) Handles PanData.MouseEnter
        AniStart({
                 AaOpacity(BtnEdit, 1 - BtnEdit.Opacity, 80),
                 AaHeight(BtnEdit, 25.5 - BtnEdit.Height, 140,, New AniEaseOutFluent),
                 AaHeight(BtnEdit, -1.5, 50, 140, New AniEaseInFluent),
                 AaOpacity(BtnExit, 1 - BtnExit.Opacity, 80),
                 AaHeight(BtnExit, 25.5 - BtnExit.Height, 140,, New AniEaseOutFluent),
                 AaHeight(BtnExit, -1.5, 50, 140, New AniEaseInFluent)
        }, "PageLoginJdycSkin Button")
    End Sub
    Private Sub PageLoginJdycSkin_MouseLeave(sender As Object, e As MouseEventArgs) Handles PanData.MouseLeave
        AniStart({
                 AaOpacity(BtnEdit, -BtnEdit.Opacity, 120,, New AniEaseInFluent),
                 AaHeight(BtnEdit, 14 - BtnEdit.Height, 120,, New AniEaseInFluent),
                 AaOpacity(BtnExit, -BtnExit.Opacity, 120,, New AniEaseOutFluent),
                 AaHeight(BtnExit, 14 - BtnExit.Height, 120,, New AniEaseInFluent)
        }, "PageLoginJdycSkin Button")
    End Sub

    Private Sub BtnEdit_Click(sender As Object, e As EventArgs) Handles BtnEdit.Click
        '换角色：清掉缓存的角色，启动加载器会重新弹窗让玩家选
        If McLoginLoader.State = LoadState.Loading Then
            Hint("正在登录中，请稍后再更换角色！", HintType.Red)
            Return
        End If
        Hint("正在尝试更换，请稍候！")
        Settings.Set("CacheJdycPlayerPid", 0)
        Settings.Set("CacheJdycPlayerName", "")
        Settings.Set("CacheJdycPlayerUuid", "")
        RunInThread(
        Sub()
            Try
                McLoginLoader.WaitForExit(GetLoginData(), IsForceRestart:=True)
                RunInUi(Sub() Reload(True))
            Catch ex As Exception
                Logger.Error(ex, "更换角色失败", LogBehavior.Toast)
            End Try
        End Sub)
    End Sub
    Public Shared Sub ExitLogin() Handles BtnExit.Click
        JdycLogout()
        McLoginJdycLoader.Input = Nothing
        FrmLaunchLeft.RefreshPage(False, True)
    End Sub

    Private Sub Skin_Click(sender As Object, e As MouseButtonEventArgs) Handles Skin.Click
        '点皮肤打开皮肤站衣柜，和 AuthSkin 的行为对应
        OpenWebsite(JdycSite & "/user/closet")
    End Sub

End Class
