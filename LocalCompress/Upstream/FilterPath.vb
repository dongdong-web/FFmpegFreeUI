' Path escaping retained from FFmpegFreeUI/功能/Module1.vb (MIT, 1059 Studio).
' The desktop module also contains UI and settings code, so only this pure helper
' is included in the local-only assembly.
Friend Module FilterPath
    Public Function 将路径转换为FFmpeg滤镜接受的格式(path As String) As String
        If String.IsNullOrEmpty(path) Then Return path
        If path.StartsWith("\\") Then
            Return "\\" & path.Substring(2).Replace("\", "\\")
        End If
        If path.Length >= 2 AndAlso path(1) = ":"c Then
            Return path.Substring(0, 1) & "\:" & path.Substring(2).Replace("\", "\\")
        End If
        Return path.Replace("\", "\\")
    End Function
End Module
