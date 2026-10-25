Public Class PageSetupJdyc

    Private PlayersCache As JArray

    Private Sub PageSetupJdyc_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        Skin.Loader = PageLaunchLeft.SkinJdyc
        RefreshAccount()
        RefreshPlayers()
    End Sub

    Public Sub Reset()
        RefreshAccount()
        RefreshPlayers()
    End Sub

    Private Sub RefreshAccount()
        If Not JdycIsLoggedIn() Then
            LabAccountName.Text = "未登录皮肤站"
            LabAccountEmail.Text = "启动器启动时会弹出皮肤站登录窗口，完成登录和学生认证后这里才会显示账号。"
            LabStudentStatus.Text = ""
            BtnLogout.IsEnabled = False
            Return
        End If
        LabAccountName.Text = Settings.Get(Of String)("CacheJdycName")
        LabAccountEmail.Text = Settings.Get(Of String)("CacheJdycEmail")
        LabStudentStatus.Text = "学生认证：已通过"
        BtnLogout.IsEnabled = True
    End Sub

    Private Function SelectedPlayer() As JToken
        If ComboPlayer.SelectedItem Is Nothing Then Return Nothing
        Dim Item = CType(ComboPlayer.SelectedItem, MyComboBoxItem)
        For Each P In PlayersCache
            If P.Value(Of Integer)("pid") = CInt(Item.Tag) Then Return P
        Next
        Return Nothing
    End Function

    Private Sub RefreshPlayers()
        If Not JdycIsLoggedIn() Then
            ComboPlayer.Items.Clear()
            PlayersCache = Nothing
            Return
        End If
        RunInNewThread(
        Sub()
            Try
                Dim Players = JdycPlayers()
                RunInUi(
                Sub()
                    PlayersCache = Players
                    ComboPlayer.Items.Clear()
                    For Each P In Players
                        Dim Item As New MyComboBoxItem With {.Content = P.Value(Of String)("name"), .Tag = P.Value(Of Integer)("pid")}
                        ComboPlayer.Items.Add(Item)
                        If P.Value(Of Integer)("pid") = Settings.Get(Of Integer)("CacheJdycPlayerPid") Then
                            ComboPlayer.SelectedItem = Item
                        End If
                    Next
                    If ComboPlayer.SelectedItem Is Nothing AndAlso ComboPlayer.Items.Count > 0 Then
                        ComboPlayer.SelectedIndex = 0
                    End If
                    RefreshSkinPreview()
                End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "加载皮肤站角色列表失败")
                RunInUi(Sub() Hint("加载皮肤站角色列表失败：" & ex.Message, HintType.Red))
            End Try
        End Sub, "Jdyc Players")
    End Sub

    Private Sub RefreshSkinPreview()
        Dim P = SelectedPlayer()
        If P Is Nothing Then Return
        Settings.Set("CacheJdycPlayerPid", P.Value(Of Integer)("pid"))
        Settings.Set("CacheJdycPlayerName", P.Value(Of String)("name"))
        Settings.Set("CacheJdycPlayerUuid", "")
        '皮肤预览走 Yggdrasil profile：先签一个会话拿到 UUID，再用皮肤加载器画
        RunInNewThread(
        Sub()
            Try
                Dim Session = JdycYggdrasilSession(P.Value(Of Integer)("pid"))
                RunInUi(Sub() Skin.Loader.Start())
            Catch ex As Exception
                Logger.Warn(ex, "加载皮肤站皮肤预览失败")
            End Try
        End Sub, "Jdyc Skin Preview")
    End Sub

    Private Sub ComboPlayer_SelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles ComboPlayer.SelectionChanged
        If ComboPlayer.SelectedItem Is Nothing Then Return
        RefreshSkinPreview()
    End Sub

    Private Sub BtnRefreshAccount_Click(sender As Object, e As EventArgs) Handles BtnRefreshAccount.Click
        RefreshAccount()
        RefreshPlayers()
    End Sub
    Private Sub BtnOpenSite_Click(sender As Object, e As EventArgs) Handles BtnOpenSite.Click
        OpenWebsite(JdycSite)
    End Sub
    Private Sub BtnLogout_Click(sender As Object, e As EventArgs) Handles BtnLogout.Click
        If MyMsgBox("退出皮肤站登录后，启动器下次启动时会重新弹出登录窗口。确定退出吗？", "退出登录", "退出", "取消", IsWarn:=True) <> 1 Then Return
        JdycLogout()
        RefreshAccount()
        RefreshPlayers()
        FrmLaunchLeft.RefreshPage(False, True)
    End Sub

    Private Sub ChangeTexture(Kind As String)
        Dim P = SelectedPlayer()
        If P Is Nothing Then
            Hint("请先选择一个角色", HintType.Red)
            Return
        End If
        RunInNewThread(
        Sub()
            Try
                Dim Closet = JdycCloset(If(Kind = "skin", "skin", "cape"))
                If Closet Is Nothing OrElse Closet.Count = 0 Then
                    RunInUi(Sub() Hint("衣柜里没有可用的" & If(Kind = "skin", "皮肤", "披风") & "，请先上传一个。", HintType.Red))
                    Return
                End If
                Dim SelectedTid As Integer = -1
                RunInUiWait(
                Sub()
                    Dim SelectionControl As New List(Of IMyRadio)
                    For Each T In Closet
                        SelectionControl.Add(New MyRadioBox With {.Text = T("item_name").ToString})
                    Next
                    Dim Index As Integer? = MyMsgBoxSelect(SelectionControl, "选择" & If(Kind = "skin", "皮肤", "披风"))
                    If Index.HasValue AndAlso Index.Value >= 0 Then SelectedTid = Closet(Index.Value).Value(Of Integer)("texture_tid")
                End Sub)
                If SelectedTid < 0 Then Return
                Dim SkinTid = P.Value(Of Integer)("tid_skin")
                Dim CapeTid = P.Value(Of Integer)("tid_cape")
                If Kind = "skin" Then SkinTid = SelectedTid Else CapeTid = SelectedTid
                JdycSetPlayerTexture(P.Value(Of Integer)("pid"), SkinTid, CapeTid)
                RunInUi(
                Sub()
                    Hint("已更换" & If(Kind = "skin", "皮肤", "披风") & "。")
                    RefreshPlayers()
                End Sub)
            Catch ex As Exception
                Logger.Error(ex, "更换皮肤/披风失败", LogBehavior.Toast)
            End Try
        End Sub, "Jdyc Change Texture")
    End Sub
    Private Sub BtnChangeSkin_Click(sender As Object, e As EventArgs) Handles BtnChangeSkin.Click
        ChangeTexture("skin")
    End Sub
    Private Sub BtnChangeCape_Click(sender As Object, e As EventArgs) Handles BtnChangeCape.Click
        ChangeTexture("cape")
    End Sub
    Private Sub BtnClearTexture_Click(sender As Object, e As EventArgs) Handles BtnClearTexture.Click
        Dim P = SelectedPlayer()
        If P Is Nothing Then
            Hint("请先选择一个角色", HintType.Red)
            Return
        End If
        RunInNewThread(
        Sub()
            Try
                JdycSetPlayerTexture(P.Value(Of Integer)("pid"), 0, 0)
                RunInUi(Sub() Hint("已清除皮肤和披风。"))
            Catch ex As Exception
                Logger.Error(ex, "清除皮肤/披风失败", LogBehavior.Toast)
            End Try
        End Sub, "Jdyc Clear Texture")
    End Sub

    Private Sub UploadTexture(Kind As String)
        Dim P = SelectedPlayer()
        If P Is Nothing Then
            Hint("请先选择一个角色", HintType.Red)
            Return
        End If
        Dim Dialog As New Microsoft.Win32.OpenFileDialog With {.Filter = "PNG 图片|*.png", .Title = "选择" & If(Kind = "skin", "皮肤", "披风") & "图片"}
        If Dialog.ShowDialog() <> True Then Return
        Dim FilePath = Dialog.FileName
        Dim TextureType = If(Kind = "cape", "cape", "steve")
        RunInNewThread(
        Sub()
            Try
                Dim Tid = JdycUploadTexture(FilePath, TextureType, P.Value(Of String)("name") & If(Kind = "cape", "-cape", ""))
                Dim SkinTid = P.Value(Of Integer)("tid_skin")
                Dim CapeTid = P.Value(Of Integer)("tid_cape")
                If Kind = "skin" Then SkinTid = Tid Else CapeTid = Tid
                JdycSetPlayerTexture(P.Value(Of Integer)("pid"), SkinTid, CapeTid)
                RunInUi(
                Sub()
                    Hint("上传并更换成功。")
                    RefreshPlayers()
                End Sub)
            Catch ex As Exception
                Logger.Error(ex, "上传皮肤/披风失败", LogBehavior.Toast)
            End Try
        End Sub, "Jdyc Upload Texture")
    End Sub
    Private Sub BtnUploadSkin_Click(sender As Object, e As EventArgs) Handles BtnUploadSkin.Click
        UploadTexture("skin")
    End Sub
    Private Sub BtnUploadCape_Click(sender As Object, e As EventArgs) Handles BtnUploadCape.Click
        UploadTexture("cape")
    End Sub

End Class
