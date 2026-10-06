' Pure helpers shared by the desktop preset panel and local-only preset compiler.
Partial Public Class 预设管理_v6

    Private Shared Function 已设置(ParamArray 值() As String) As Boolean
        Return 值 IsNot Nothing AndAlso 值.Any(Function(x) Not String.IsNullOrWhiteSpace(x))
    End Function

    Private Shared Function 抽帧参数已设置(a As 预设数据_v6) As Boolean
        If a Is Nothing Then Return False
        Return 已设置(a.视频参数_抽帧_max, a.视频参数_抽帧_keep, a.视频参数_抽帧_hi, a.视频参数_抽帧_lo, a.视频参数_抽帧_frac)
    End Function

    Private Shared Function 超分单片有设置(单片 As 预设数据_v6.超分数据单片结构) As Boolean
        If 单片 Is Nothing Then Return False
        Return 已设置(单片.目标宽度, 单片.目标高度, 单片.上采样算法, 单片.下采样算法, 单片.抗振铃强度, 单片.着色器文件路径)
    End Function

    Private Shared Function 字幕颜色已设置(颜色 As 预设数据_v6.烧字幕专用颜色类型) As Boolean
        If 颜色 Is Nothing Then Return False
        Return 颜色.已设置 OrElse 颜色.A <> 255 OrElse 颜色.R <> 0 OrElse 颜色.G <> 0 OrElse 颜色.B <> 0
    End Function

    Private Shared Function 限制颜色通道(value As Integer) As Integer
        Return Math.Min(255, Math.Max(0, value))
    End Function

    Private Shared Function 获取目标流类型(标识符 As 预设数据_v6.滤镜排序单片结构.标识符枚举) As 预设数据_v6.滤镜排序单片结构.流类型
        Select Case 标识符
            Case 预设数据_v6.滤镜排序单片结构.标识符枚举.音频响度标准化,
                 预设数据_v6.滤镜排序单片结构.标识符枚举.音频格式转换,
                 预设数据_v6.滤镜排序单片结构.标识符枚举.音频重采样,
                 预设数据_v6.滤镜排序单片结构.标识符枚举.自定义音频滤镜
                Return 预设数据_v6.滤镜排序单片结构.流类型.音频
            Case Else
                Return 预设数据_v6.滤镜排序单片结构.流类型.视频
        End Select
    End Function

    Public Shared Function 获取滤镜显示名称(标识符 As 预设数据_v6.滤镜排序单片结构.标识符枚举) As String
        If 标识符 = 预设数据_v6.滤镜排序单片结构.标识符枚举.未设置 Then Return "未设置"
        Select Case 标识符
            Case 预设数据_v6.滤镜排序单片结构.标识符枚举.NV_FRUC : Return "NVIDIA Vulkan FRUC"
        End Select
        Return 标识符.ToString()
    End Function
End Class
