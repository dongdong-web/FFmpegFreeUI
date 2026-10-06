Imports System.Globalization

' A narrow product boundary over the actual source-linked 3FUI preset compiler.
' User-supplied preset JSON, arbitrary arguments and script engines are not exposed.
Public NotInheritable Class PresetCompiler
    Public Shared Function BuildArguments(input As String, output As String, quality As Integer,
                                          Optional maxShortEdge As Integer = 0,
                                          Optional reduceNoise As Boolean = False,
                                          Optional normalizeAudio As Boolean = False,
                                          Optional hasAudio As Boolean = True,
                                          Optional scene As Integer = -1,
                                          Optional copyAudio As Boolean = False) As IReadOnlyList(Of String)
        Return BuildPlan(input, output, quality, maxShortEdge, reduceNoise, normalizeAudio, hasAudio, scene:=scene, copyAudio:=copyAudio)(0)
    End Function

    Public Shared Function BuildPlan(input As String, output As String, quality As Integer,
                                    Optional maxShortEdge As Integer = 0,
                                    Optional reduceNoise As Boolean = False,
                                    Optional normalizeAudio As Boolean = False,
                                    Optional hasAudio As Boolean = True,
                                    Optional videoBitrate As Integer = 0,
                                    Optional passLog As String = "",
                                    Optional scene As Integer = -1,
                                    Optional copyAudio As Boolean = False,
                                    Optional sceneQualityOffset As Integer = 0,
                                    Optional preserveTiming As Boolean = False) As IReadOnlyList(Of IReadOnlyList(Of String))
        If quality < 0 OrElse quality > 2 Then Throw New ArgumentOutOfRangeException(NameOf(quality))
        If Not {0, 720, 1080}.Contains(maxShortEdge) Then Throw New ArgumentOutOfRangeException(NameOf(maxShortEdge))
        If scene < -1 OrElse scene > 1 Then Throw New ArgumentOutOfRangeException(NameOf(scene))
        If sceneQualityOffset < 0 OrElse sceneQualityOffset > If(scene = 1, 2, 1) OrElse
            (sceneQualityOffset <> 0 AndAlso (scene < 0 OrElse videoBitrate <> 0)) Then
            Throw New ArgumentOutOfRangeException(NameOf(sceneQualityOffset))
        End If
        If scene >= 0 AndAlso (maxShortEdge <> 0 OrElse reduceNoise OrElse normalizeAudio) Then
            Throw New ArgumentException("Scene profiles preserve size and do not apply noise or loudness filters.")
        End If
        Dim crf = If(scene = -1, {"20", "25", "30"}(quality), (If(scene = 0, 20, 18) + sceneQualityOffset).ToString(CultureInfo.InvariantCulture))
        Dim preset As New 预设数据_v6 With {
            .输出容器 = "mp4",
            .视频参数_编码器_类型 = 预设数据_v6.视频编码器类型.视频,
            .视频参数_编码器_分类名称 = "H.264/AVC",
            .视频参数_编码器_具体编码 = "libx264",
            .视频参数_编码器_编码预设 = If(scene >= 0, "slow", "medium"),
            .视频参数_比特率_控制方式 = 预设数据_v6.视频全局质量控制方式.CRF,
            .视频参数_质量控制_参数名 = "crf",
            .视频参数_质量控制_值 = crf,
            .视频参数_色彩管理_像素格式 = "yuv420p",
            .视频参数_分辨率自动计算_宽度 = "trunc(iw/2)*2",
            .视频参数_分辨率自动计算_高度 = "trunc(ih/2)*2",
            .流控制_将视频参数应用于指定流 = {"0:v:0"},
            .流控制_将音频参数应用于指定流 = If(hasAudio, {"0:a:0"}, Array.Empty(Of String)()),
            .音频参数_编码器_代号 = If(hasAudio, If(copyAudio, "audio.copy", "aac.native"), "audio.disable"),
            .音频参数_比特率 = If(hasAudio AndAlso Not copyAudio, If(scene >= 0 AndAlso videoBitrate = 0, "192k", "128k"), ""),
            .流控制_元数据选项 = 预设数据_v6.流控制元数据选项.清除元数据,
            .流控制_章节选项 = 预设数据_v6.流控制章节选项.清除章节
        }
        If maxShortEdge > 0 Then
            Dim edge = maxShortEdge.ToString(CultureInfo.InvariantCulture)
            ' Cap the short edge (landscape and portrait), preserve aspect ratio,
            ' and never upscale. The upstream builder supplies the scale filter.
            preset.视频参数_分辨率自动计算_宽度 = $"if(gte(iw\,ih)\,-2\,trunc(min(iw\,{edge})/2)*2)"
            preset.视频参数_分辨率自动计算_高度 = $"if(gte(iw\,ih)\,trunc(min(ih\,{edge})/2)*2\,-2)"
        End If
        If reduceNoise Then
            preset.视频参数_降噪_方式 = 预设数据_v6.降噪方式.hqdn3d
            preset.视频参数_降噪_参数1 = "1.5"
            preset.视频参数_降噪_参数2 = "1.5"
            preset.视频参数_降噪_参数3 = "3"
            preset.视频参数_降噪_参数4 = "3"
        End If
        If normalizeAudio AndAlso hasAudio Then
            preset.音频参数_响度标准化_启用调整目标响度 = True
            preset.音频参数_响度标准化_目标响度 = "-16"
            preset.音频参数_响度标准化_启用调整峰值电平 = True
            preset.音频参数_响度标准化_峰值电平 = "-1.5"
        End If

        If videoBitrate < 0 Then Throw New ArgumentOutOfRangeException(NameOf(videoBitrate))
        If videoBitrate > 0 Then
            If String.IsNullOrWhiteSpace(passLog) Then Throw New ArgumentException("A private pass log is required.", NameOf(passLog))
            preset.视频参数_比特率_控制方式 = 预设数据_v6.视频全局质量控制方式.TPE
            preset.视频参数_比特率_基础 = videoBitrate.ToString(CultureInfo.InvariantCulture)
        End If
        Dim stages = 预设管理_v6.生成阶段化命令行(preset, input, output)
        If stages.Count <> If(videoBitrate > 0, 2, 1) Then Throw New InvalidOperationException("Unexpected encoding stages.")
        Dim plan As New List(Of IReadOnlyList(Of String))
        For Each stage In stages
            Dim firstPass = stage.阶段 = 预设数据_v6.命令行阶段.二次编码第一遍
            Dim arguments = 启动参数响应_v6.拆分命令行(stage.命令行)
            If arguments.Count = 0 OrElse arguments.Last() <> If(firstPass, "NUL", output) OrElse arguments.Where(Function(x) x = "-i").Count() <> 1 Then
                Throw New InvalidOperationException("Unexpected input or output in the upstream compression plan.")
            End If
            ' The desktop compiler overwrites outputs by default; the local edition
            ' never does. Input protocols and stream metadata remain constrained.
            Dim overwrite = arguments.IndexOf("-y")
            If overwrite < 0 Then Throw New InvalidOperationException("Missing overwrite policy in upstream plan.")
            arguments(overwrite) = "-n"
            If videoBitrate > 0 Then
                Dim logIndex = arguments.FindIndex(Function(x) x.StartsWith("-passlogfile", StringComparison.Ordinal))
                If logIndex < 0 OrElse logIndex + 1 >= arguments.Count Then Throw New InvalidOperationException("Missing upstream pass log.")
                arguments(logIndex + 1) = passLog
            End If
            arguments.InsertRange(0, {"-nostdin", "-protocol_whitelist", "file,pipe"})
            If scene >= 0 OrElse preserveTiming Then arguments.InsertRange(arguments.Count - 1, {"-fps_mode", "passthrough"})
            If Not firstPass Then arguments.InsertRange(arguments.Count - 1, {"-map_metadata:s", "-1", "-movflags", "+faststart"})
            arguments.InsertRange(arguments.Count - 1, {"-progress", "pipe:1", "-nostats"})
            plan.Add(arguments.AsReadOnly())
        Next
        Return plan.AsReadOnly()
    End Function
End Class
