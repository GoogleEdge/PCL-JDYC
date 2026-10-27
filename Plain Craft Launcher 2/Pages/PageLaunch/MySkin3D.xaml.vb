Imports System.Windows.Media.Media3D
Imports System.Windows.Threading

''' <summary>
''' 3D 皮肤预览：把 64 宽皮肤贴到方块人模型上，可拖动旋转，闲置时缓慢自转。
''' 贴图按 1 像素一格细分，叠加层（帽子、外套等）按像素透明度合成到同一表面，避免透明层遮挡问题。
''' 仅支持 Steve 体型（经典 4 格宽手臂）；64x32 老皮肤的左侧肢体按右侧镜像。
''' </summary>
Public Class MySkin3D

    Public Loader As LoaderTask(Of (String, String), String)
    Private SkinAddress As String

    ''' <summary>
    ''' 载入皮肤（与 MySkin.Load 相同的守卫：文件存在、尺寸合法）。
    ''' </summary>
    Public Sub Load()
        Try
            '检查文件存在
            SkinAddress = Loader.Output
            If String.IsNullOrEmpty(SkinAddress) Then Throw New Exception("皮肤加载器 " & Loader.Name & " 没有输出")
            If Not SkinAddress.StartsWithF(PathImage) AndAlso Not FileUtils.Exists(SkinAddress) Then Throw New FileNotFoundException("皮肤文件未找到", SkinAddress)
            '加载
            Dim Image As MyBitmap
            Try
                Image = New MyBitmap(SkinAddress)
            Catch ex As Exception
                Logger.Error(ex, $"皮肤文件已损坏：{SkinAddress}", LogBehavior.Toast)
                FileUtils.Delete(SkinAddress)
                Return
            End Try
            '只接受 64 宽皮肤：64x64 或 64x32；128 等高清按 64 网格采样
            Dim W = Image.Pic.Width
            Dim H = Image.Pic.Height
            If W < 64 OrElse W Mod 64 <> 0 Then
                Clear()
                Throw New Exception("皮肤宽度不是 64 的倍数：" & W)
            End If
            Dim Scale = W \ 64
            Dim GH = H \ Scale
            If H Mod Scale <> 0 OrElse (GH <> 64 AndAlso GH <> 32) Then
                Clear()
                Throw New Exception("皮肤高度不是 32 或 64 的倍数：" & H)
            End If
            BuildModel(Image.Pic, W, H, Scale, GH)
            Logger.Info($"载入 3D 皮肤成功：{Loader.Name}")
        Catch ex As Exception
            Logger.Error(ex, $"载入 3D 皮肤失败（{If(SkinAddress, "null")},{Loader.Name}）", LogBehavior.Toast)
        End Try
    End Sub
    ''' <summary>
    ''' 清空预览。
    ''' </summary>
    Public Sub Clear()
        SkinAddress = ""
        ModelView.Content = Nothing
    End Sub

    '-------------------- 模型生成 --------------------

    Private Sub BuildModel(Pic As BitmapSource, W As Integer, H As Integer, Scale As Integer, GH As Integer)
        '取 Bgra32 像素，供叠加层透明判断
        Dim Formatted As New FormatConvertedBitmap(Pic, PixelFormats.Bgra32, Nothing, 0)
        Dim Stride = W * 4
        Dim Pixels(Stride * H - 1) As Byte
        Formatted.CopyPixels(Pixels, Stride, 0)
        Dim Mesh As New MeshGeometry3D()
        Dim Is64 = (GH = 64)
        '头（帽子叠加层两种布局都有）
        AddBox(Mesh, Pixels, W, H, Scale, -4, 4, 24, 32, -4, 4, 0, 0, 8, 8, 8, 32, 0)
        '身体（外套叠加层只在 64 高布局里有）
        AddBox(Mesh, Pixels, W, H, Scale, -4, 4, 12, 24, -2, 2, 16, 16, 8, 4, 12,
               If(Is64, 16, -1), If(Is64, 32, -1))
        '右腿（叠加层只在 64 高布局里有）
        AddBox(Mesh, Pixels, W, H, Scale, -4, 0, 0, 12, -2, 2, 0, 16, 4, 4, 12,
               If(Is64, 0, -1), If(Is64, 32, -1))
        '左腿：64 高用 (16,48)/(0,48)；32 高镜像右腿
        If Is64 Then
            AddBox(Mesh, Pixels, W, H, Scale, 0, 4, 0, 12, -2, 2, 16, 48, 4, 4, 12, 0, 48)
        Else
            AddBox(Mesh, Pixels, W, H, Scale, 0, 4, 0, 12, -2, 2, 0, 16, 4, 4, 12, -1, -1, True)
        End If
        '右臂（叠加层只在 64 高布局里有）
        AddBox(Mesh, Pixels, W, H, Scale, -8, -4, 12, 24, -2, 2, 40, 16, 4, 4, 12,
               If(Is64, 40, -1), If(Is64, 32, -1))
        '左臂：64 高用 (32,48)/(48,48)；32 高镜像右臂
        If Is64 Then
            AddBox(Mesh, Pixels, W, H, Scale, 4, 8, 12, 24, -2, 2, 32, 48, 4, 4, 12, 48, 48)
        Else
            AddBox(Mesh, Pixels, W, H, Scale, 4, 8, 12, 24, -2, 2, 40, 16, 4, 4, 12, -1, -1, True)
        End If
        Dim Brush As New ImageBrush(Pic)
        RenderOptions.SetBitmapScalingMode(Brush, BitmapScalingMode.NearestNeighbor)
        Dim Mat As New DiffuseMaterial(Brush)
        ModelView.Content = New GeometryModel3D(Mesh, Mat) With {.BackMaterial = Mat}
    End Sub

    ''' <summary>
    ''' 往网格里加一个方块。ox/oy 为皮肤网上该部位的原点，w/d/h 为宽/深/高（格）；
    ''' oox/ooy 为叠加层原点（小于 0 表示无叠加层）；mirror 为 64x32 左侧肢体的镜像。
    ''' 角色面朝 +Z，角色右侧为 -X；脚底 y=0。
    ''' </summary>
    Private Sub AddBox(Mesh As MeshGeometry3D, Pixels As Byte(), W As Integer, H As Integer, Scale As Integer,
                       x0 As Double, x1 As Double, y0 As Double, y1 As Double, z0 As Double, z1 As Double,
                       ox As Integer, oy As Integer, w As Integer, d As Integer, h As Integer,
                       Optional oox As Integer = -1, Optional ooy As Integer = -1, Optional mirror As Boolean = False)
        '无叠加层时保持 -1 哨兵（直接加偏移会把 -1 变成非负，误判为有叠加层）
        Dim fox = If(oox < 0, -1, oox + d), foy = If(ooy < 0, -1, ooy + d)
        Dim kix = If(oox < 0, -1, oox + d + w + d)
        Dim rix = oox, riy = foy
        Dim lix = If(oox < 0, -1, oox + d + w)
        Dim tix = fox, tiy = ooy
        Dim bix = lix, biy = ooy
        '前（+Z）
        AddFace(Mesh, Pixels, W, H, Scale,
                New Point3D(x0, y0, z1), New Point3D(x1, y0, z1), New Point3D(x1, y1, z1), New Point3D(x0, y1, z1), New Vector3D(0, 0, 1),
                ox + d, oy + d, w, h, fox, foy, mirror)
        '后（-Z）
        AddFace(Mesh, Pixels, W, H, Scale,
                New Point3D(x1, y0, z0), New Point3D(x0, y0, z0), New Point3D(x0, y1, z0), New Point3D(x1, y1, z0), New Vector3D(0, 0, -1),
                ox + d + w + d, oy + d, w, h, kix, foy, mirror)
        '右（-X，角色右侧）
        AddFace(Mesh, Pixels, W, H, Scale,
                New Point3D(x0, y0, z0), New Point3D(x0, y0, z1), New Point3D(x0, y1, z1), New Point3D(x0, y1, z0), New Vector3D(-1, 0, 0),
                ox, oy + d, d, h, rix, riy, mirror)
        '左（+X）
        AddFace(Mesh, Pixels, W, H, Scale,
                New Point3D(x1, y0, z1), New Point3D(x1, y0, z0), New Point3D(x1, y1, z0), New Point3D(x1, y1, z1), New Vector3D(1, 0, 0),
                ox + d + w, oy + d, d, h, lix, foy, mirror)
        '顶（+Y）
        AddFace(Mesh, Pixels, W, H, Scale,
                New Point3D(x0, y1, z1), New Point3D(x1, y1, z1), New Point3D(x1, y1, z0), New Point3D(x0, y1, z0), New Vector3D(0, 1, 0),
                ox + d, oy, w, d, tix, tiy, mirror)
        '底（-Y，上下翻转贴图，平时看不到）
        AddFace(Mesh, Pixels, W, H, Scale,
                New Point3D(x1, y0, z1), New Point3D(x0, y0, z1), New Point3D(x0, y0, z0), New Point3D(x1, y0, z0), New Vector3D(0, -1, 0),
                ox + d + w, oy, w, d, bix, biy, mirror, True)
    End Sub

    ''' <summary>
    ''' 往网格里加一个面，按 1 像素一格细分；每格按叠加层像素透明度决定用底层还是叠加层。
    ''' BL/BR/TR/TL 为从面外看的左下/右下/右上/左上；flipV 为底面翻转。
    ''' </summary>
    Private Sub AddFace(Mesh As MeshGeometry3D, Pixels As Byte(), W As Integer, H As Integer, Scale As Integer,
                        BL As Point3D, BR As Point3D, TR As Point3D, TL As Point3D, Normal As Vector3D,
                        rx As Integer, ry As Integer, rw As Integer, rh As Integer,
                        Optional orx As Integer = -1, Optional ory As Integer = -1,
                        Optional mirror As Boolean = False, Optional flipV As Boolean = False)
        For cy = 0 To rh - 1
            Dim RowFromBottom = rh - 1 - cy
            For cx = 0 To rw - 1
                Dim scx = If(mirror, rw - 1 - cx, cx)
                '叠加层像素不透明就用叠加层，否则用底层
                Dim UseOverlay = False
                If orx >= 0 Then
                    Dim ax = (orx + scx) * Scale, ay = (ory + cy) * Scale
                    If Pixels((ay * W + ax) * 4 + 3) > 0 Then UseOverlay = True
                End If
                Dim bx = If(UseOverlay, orx, rx), by = If(UseOverlay, ory, ry)
                Dim u0 = (bx + scx) / W, u1 = (bx + scx + 1) / W
                Dim vTop = (by + cy) / H, vBottom = (by + cy + 1) / H
                If flipV Then
                    Dim t = vTop : vTop = vBottom : vBottom = t
                End If
                Dim fu0 = cx / rw, fu1 = (cx + 1) / rw
                Dim fv0 = RowFromBottom / rh, fv1 = (RowFromBottom + 1) / rh
                Dim qBL = Bilinear(BL, BR, TL, fu0, fv0)
                Dim qBR = Bilinear(BL, BR, TL, fu1, fv0)
                Dim qTR = Bilinear(BL, BR, TL, fu1, fv1)
                Dim qTL = Bilinear(BL, BR, TL, fu0, fv1)
                Dim Base = Mesh.Positions.Count
                Mesh.Positions.Add(qBL) : Mesh.Positions.Add(qBR) : Mesh.Positions.Add(qTR) : Mesh.Positions.Add(qTL)
                Mesh.TextureCoordinates.Add(New Point(u0, vBottom))
                Mesh.TextureCoordinates.Add(New Point(u1, vBottom))
                Mesh.TextureCoordinates.Add(New Point(u1, vTop))
                Mesh.TextureCoordinates.Add(New Point(u0, vTop))
                Mesh.Normals.Add(Normal) : Mesh.Normals.Add(Normal) : Mesh.Normals.Add(Normal) : Mesh.Normals.Add(Normal)
                Mesh.TriangleIndices.Add(Base) : Mesh.TriangleIndices.Add(Base + 1) : Mesh.TriangleIndices.Add(Base + 2)
                Mesh.TriangleIndices.Add(Base) : Mesh.TriangleIndices.Add(Base + 2) : Mesh.TriangleIndices.Add(Base + 3)
            Next
        Next
    End Sub

    Private Shared Function Bilinear(BL As Point3D, BR As Point3D, TL As Point3D, u As Double, v As Double) As Point3D
        Return New Point3D(BL.X + (BR.X - BL.X) * u + (TL.X - BL.X) * v,
                           BL.Y + (BR.Y - BL.Y) * u + (TL.Y - BL.Y) * v,
                           BL.Z + (BR.Z - BL.Z) * u + (TL.Z - BL.Z) * v)
    End Function

    '-------------------- 旋转与自转 --------------------

    Private Timer As DispatcherTimer
    Private Dragging As Boolean = False
    Private LastPos As Point

    Private Sub MySkin3D_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        If Timer Is Nothing Then
            Timer = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(50)}
            AddHandler Timer.Tick, AddressOf Timer_Tick
        End If
        Timer.Start()
    End Sub

    Private Sub MySkin3D_Unloaded(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        Timer?.Stop()
    End Sub

    Private Sub Timer_Tick(sender As Object, e As EventArgs)
        If Not Dragging Then RotY.Angle = (RotY.Angle + 0.4) Mod 360
    End Sub

    Private Sub Pan_MouseLeftButtonDown(sender As Object, e As MouseButtonEventArgs) Handles Me.MouseLeftButtonDown
        Dragging = True
        LastPos = e.GetPosition(Me)
        CaptureMouse()
    End Sub

    Private Sub Pan_MouseMove(sender As Object, e As MouseEventArgs) Handles Me.MouseMove
        If Not Dragging Then Return
        Dim P = e.GetPosition(Me)
        RotY.Angle = (RotY.Angle + (P.X - LastPos.X) * 0.5) Mod 360
        RotX.Angle = Math.Max(-70, Math.Min(70, RotX.Angle - (P.Y - LastPos.Y) * 0.5))
        LastPos = P
    End Sub

    Private Sub Pan_MouseLeftButtonUp(sender As Object, e As MouseButtonEventArgs) Handles Me.MouseLeftButtonUp
        Dragging = False
        ReleaseMouseCapture()
    End Sub

End Class
