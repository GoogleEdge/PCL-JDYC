Imports System.Diagnostics
Imports System.IO
Imports System.Net.Sockets
Imports System.Text
Imports Newtonsoft.Json.Linq

''' <summary>
''' MC Java 版服务器状态检测（Server List Ping，参考 wiki.vg 与 mcstatus 等开源实现）。
''' 流程：TCP 连接 → 握手包（下一状态 = 1，状态查询）→ 状态请求包 → 状态响应 JSON →
''' Ping 包（8 字节时间戳）→ Pong 回显，往返时间即延迟。
''' 只读服务器状态，不登录、不进游戏、不改服务器；不触 UI，任何线程调用都安全。
''' </summary>
Public Class McPingResult
    Public Success As Boolean
    Public LatencyMs As Long
    Public VersionName As String
    Public PlayersOnline As Integer
    Public PlayersMax As Integer
    Public ErrorMessage As String
End Class

Public Module ModMcPing

    ''' <summary>向一台 Java 版 MC 服务器发状态检测，返回是否通过与延迟（毫秒）。</summary>
    Public Function McPingServer(Host As String, Port As Integer, TimeoutMs As Integer) As McPingResult
        Dim Result As New McPingResult()
        If Port < 1 OrElse Port > 65535 Then
            Result.ErrorMessage = "端口无效"
            Return Result
        End If
        Dim Client As TcpClient = Nothing
        Try
            Client = New TcpClient()
            Dim ConnectTask = Client.ConnectAsync(Host, Port)
            Try
                If Not ConnectTask.Wait(TimeoutMs) Then Throw New TimeoutException("连接超时")
            Catch ex As AggregateException
                Throw If(ex.InnerException, DirectCast(ex, Exception))
            End Try
            Client.SendTimeout = TimeoutMs
            Client.ReceiveTimeout = TimeoutMs
            Dim Stream = Client.GetStream()
            ' 握手包：ID 0x00，协议版本 -1（状态查询用任意版本），地址，端口，下一状态 1。
            Dim Handshake As New MemoryStream()
            WriteVarInt(Handshake, 0)
            WriteVarInt(Handshake, -1)
            WriteMcString(Handshake, Host)
            WriteUShort(Handshake, Port)
            WriteVarInt(Handshake, 1)
            WritePacket(Stream, Handshake.ToArray())
            ' 状态请求包：ID 0x00，无负载。
            WritePacket(Stream, New Byte() {0})
            Dim StatusSw As Stopwatch = Stopwatch.StartNew()
            Dim Resp = ReadPacket(Stream)
            StatusSw.Stop()
            If Resp.Id <> 0 Then Throw New InvalidDataException("状态响应包 ID 不是 0x00")
            Dim Pos As Integer = 0
            Dim StatusJson = JObject.Parse(ReadMcString(Resp.Payload, Pos))
            Dim Ver = StatusJson("version")
            If Ver IsNot Nothing AndAlso Ver("name") IsNot Nothing Then Result.VersionName = Ver("name").ToString()
            Dim Players = StatusJson("players")
            If Players IsNot Nothing Then
                Integer.TryParse(If(Players("online") Is Nothing, "", Players("online").ToString()), Result.PlayersOnline)
                Integer.TryParse(If(Players("max") Is Nothing, "", Players("max").ToString()), Result.PlayersMax)
            End If
            ' Ping / Pong：发 8 字节时间戳，服务器原样回显，往返时间即延迟。
            ' Pong 失败也不判失败（状态响应已证明服务器可达），改用状态响应耗时。
            Try
                Dim PingPayload = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                Dim PingSw As Stopwatch = Stopwatch.StartNew()
                Dim PingPacket As New MemoryStream()
                WriteVarInt(PingPacket, 1)
                WriteLongBE(PingPacket, PingPayload)
                WritePacket(Stream, PingPacket.ToArray())
                Dim Pong = ReadPacket(Stream)
                PingSw.Stop()
                If Pong.Id <> 1 Then Throw New InvalidDataException("Pong 包 ID 不是 0x01")
                Dim PongPos As Integer = 0
                If ReadLongBE(Pong.Payload, PongPos) <> PingPayload Then Throw New InvalidDataException("Pong 负载与 Ping 不符")
                Result.LatencyMs = PingSw.ElapsedMilliseconds
            Catch ex As Exception
                Logger.Warn(ex, "MC 检测 Pong 失败，改用状态响应耗时")
                Result.LatencyMs = StatusSw.ElapsedMilliseconds
            End Try
            Result.Success = True
        Catch ex As TimeoutException
            Result.ErrorMessage = "连接超时（" & (TimeoutMs \ 1000) & " 秒无响应）"
        Catch ex As SocketException
            Select Case ex.SocketErrorCode
                Case SocketError.ConnectionRefused
                    Result.ErrorMessage = "连接被拒绝（游戏服务器未开服或转发未接通）"
                Case SocketError.HostNotFound
                    Result.ErrorMessage = "域名解析失败"
                Case SocketError.TimedOut
                    Result.ErrorMessage = "连接超时"
                Case Else
                    Result.ErrorMessage = "连接失败：" & ex.Message
            End Select
        Catch ex As IOException
            Result.ErrorMessage = "读取响应失败（连接超时或中断）"
        Catch ex As InvalidDataException
            Result.ErrorMessage = "响应无效（可能不是 Java 版 MC 服务器）"
        Catch ex As Newtonsoft.Json.JsonException
            Result.ErrorMessage = "响应无效（服务器状态不是合法 JSON）"
        Catch ex As Exception
            Logger.Warn(ex, "MC 服务器状态检测失败")
            Result.ErrorMessage = "检测失败：" & ex.Message
        Finally
            If Client IsNot Nothing Then
                Try
                    Client.Close()
                Catch
                    ' 关闭失败无需处理
                End Try
            End If
        End Try
        Return Result
    End Function

    '-------------------- MC 协议编码（VarInt / 字符串 / 大端整数） --------------------

    Private Sub WriteVarInt(Stream As MemoryStream, Value As Integer)
        Dim U As UInteger = CUInt(Value)
        While True
            Dim B As UInteger = U And &H7FUI
            U >>= 7
            If U = 0 Then
                Stream.WriteByte(CByte(B))
                Return
            End If
            Stream.WriteByte(CByte(B Or &H80UI))
        End While
    End Sub

    Private Function ReadVarInt(Data As Byte(), ByRef Pos As Integer) As Integer
        Dim Result As Long = 0
        Dim Shift As Integer = 0
        While True
            If Pos >= Data.Length Then Throw New IOException("数据包不完整")
            Dim B = Data(Pos)
            Pos += 1
            Result = Result Or (CLng(B And &H7F) << Shift)
            If (B And &H80) = 0 Then Exit While
            Shift += 7
            If Shift >= 35 Then Throw New InvalidDataException("VarInt 过长")
        End While
        If Result >= 2147483648L Then Result -= 4294967296L
        Return CInt(Result)
    End Function

    Private Function ReadVarIntFromStream(Stream As NetworkStream) As Integer
        Dim Result As Long = 0
        Dim Shift As Integer = 0
        While True
            Dim B = Stream.ReadByte()
            If B < 0 Then Throw New IOException("连接已关闭")
            Result = Result Or (CLng(B And &H7F) << Shift)
            If (B And &H80) = 0 Then Exit While
            Shift += 7
            If Shift >= 35 Then Throw New InvalidDataException("VarInt 过长")
        End While
        If Result >= 2147483648L Then Result -= 4294967296L
        Return CInt(Result)
    End Function

    Private Sub WriteMcString(Stream As MemoryStream, Text As String)
        Dim Bytes = Encoding.UTF8.GetBytes(Text)
        WriteVarInt(Stream, Bytes.Length)
        Stream.Write(Bytes, 0, Bytes.Length)
    End Sub

    Private Function ReadMcString(Data As Byte(), ByRef Pos As Integer) As String
        Dim Length = ReadVarInt(Data, Pos)
        If Length < 0 OrElse Pos + Length > Data.Length Then Throw New IOException("字符串长度异常")
        Dim Text = Encoding.UTF8.GetString(Data, Pos, Length)
        Pos += Length
        Return Text
    End Function

    Private Sub WriteUShort(Stream As MemoryStream, Value As Integer)
        Stream.WriteByte(CByte((Value >> 8) And &HFF))
        Stream.WriteByte(CByte(Value And &HFF))
    End Sub

    Private Sub WriteLongBE(Stream As MemoryStream, Value As Long)
        For I = 7 To 0 Step -1
            Stream.WriteByte(CByte((Value >> (I * 8)) And &HFF))
        Next
    End Sub

    Private Function ReadLongBE(Data As Byte(), ByRef Pos As Integer) As Long
        If Pos + 8 > Data.Length Then Throw New IOException("数据包不完整")
        Dim Result As Long = 0
        For I = 0 To 7
            Result = (Result << 8) Or Data(Pos)
            Pos += 1
        Next
        Return Result
    End Function

    '-------------------- 数据包收发（长度 VarInt + ID VarInt + 负载） --------------------

    Private Sub WritePacket(Stream As NetworkStream, Payload As Byte())
        Dim Frame As New MemoryStream()
        WriteVarInt(Frame, Payload.Length)
        Frame.Write(Payload, 0, Payload.Length)
        Dim Bytes = Frame.ToArray()
        Stream.Write(Bytes, 0, Bytes.Length)
        Stream.Flush()
    End Sub

    Private Function ReadExact(Stream As NetworkStream, Count As Integer) As Byte()
        Dim Buffer(Count - 1) As Byte
        Dim Read As Integer = 0
        While Read < Count
            Dim N = Stream.Read(Buffer, Read, Count - Read)
            If N <= 0 Then Throw New IOException("连接已关闭")
            Read += N
        End While
        Return Buffer
    End Function

    Private Function ReadPacket(Stream As NetworkStream) As McPacket
        Dim Length = ReadVarIntFromStream(Stream)
        If Length <= 0 OrElse Length > 1048576 Then Throw New InvalidDataException("数据包长度异常")
        Dim Data = ReadExact(Stream, Length)
        Dim Pos As Integer = 0
        Dim Id = ReadVarInt(Data, Pos)
        Dim Payload As Byte() = New Byte() {}
        If Pos < Data.Length Then
            ReDim Payload(Data.Length - Pos - 1)
            Array.Copy(Data, Pos, Payload, 0, Payload.Length)
        End If
        Dim Packet As New McPacket()
        Packet.Id = Id
        Packet.Payload = Payload
        Return Packet
    End Function

    Private Class McPacket
        Public Id As Integer
        Public Payload As Byte()
    End Class

End Module
