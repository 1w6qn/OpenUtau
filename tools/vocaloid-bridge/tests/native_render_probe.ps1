param(
    [string]$Config=(Join-Path $PSScriptRoot '../vocaloid4.example.json'),
    [ValidateRange(1,32)][int]$EditCount=8,
    [string]$RequestPath='',
    [string]$OutputWavPath=''
)
$ErrorActionPreference='Stop'
$bridgeRoot=Split-Path -Parent $PSScriptRoot
Add-Type -Path @((Join-Path $bridgeRoot 'Core/Contracts.cs'),(Join-Path $bridgeRoot 'Core/BridgeSession.cs')) -CompilerOptions '/nullable:enable'
$configPath=[IO.Path]::GetFullPath($Config)
$settings=[VocaloidBridge.BridgeConfig]::Load($configPath)
$requestPath=if([string]::IsNullOrWhiteSpace($RequestPath)){Join-Path $bridgeRoot 'request.vocaloid4.json'}else{[IO.Path]::GetFullPath($RequestPath)}
$session=[VocaloidBridge.BridgeSession]::Get($configPath)
try {
    $cold=[Diagnostics.Stopwatch]::StartNew()
    $voices=$session.ListVoicesAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $cold.Stop()
    if (-not ($voices | Where-Object Id -CEQ $settings.VoiceId)) {throw 'Configured native Vocaloid voice ID not found.'}
    $request=[Text.Json.JsonSerializer]::Deserialize([IO.File]::ReadAllText($requestPath),[VocaloidBridge.BridgeRequest])
    $request.VoiceId=$settings.VoiceId
    $firstWatch=[Diagnostics.Stopwatch]::StartNew()
    $audio=$session.RenderAsync($request,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $firstWatch.Stop()
    $peak=0.0
    foreach($s in $audio.Samples){$peak=[Math]::Max($peak,[Math]::Abs($s))}
    if($peak -lt 0.0001){throw 'Native Vocaloid synthesis returned silent audio.'}
    if($audio.SampleRate -ne 44100){throw 'Unexpected native Vocaloid sample rate.'}
    if(-not [string]::IsNullOrWhiteSpace($OutputWavPath)) {
        $wavPath=[IO.Path]::GetFullPath($OutputWavPath)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($wavPath))|Out-Null
        $dataBytes=[int]($audio.Samples.Length*2)
        $stream=[IO.File]::Create($wavPath)
        try {
            $writer=[IO.BinaryWriter]::new($stream,[Text.Encoding]::ASCII,$false)
            try {
                $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF'));$writer.Write([int](36+$dataBytes))
                $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '));$writer.Write([int]16)
                $writer.Write([UInt16]1);$writer.Write([UInt16]1);$writer.Write([int]$audio.SampleRate)
                $writer.Write([int]($audio.SampleRate*2));$writer.Write([UInt16]2);$writer.Write([UInt16]16)
                $writer.Write([Text.Encoding]::ASCII.GetBytes('data'));$writer.Write([int]$dataBytes)
                foreach($sample in $audio.Samples) {
                    $clipped=[Math]::Max(-1.0,[Math]::Min(1.0,[double]$sample))
                    $writer.Write([Int16][Math]::Round($clipped*32767))
                }
            } finally {$writer.Dispose()}
        } finally {$stream.Dispose()}
    }
    $cacheWatch=[Diagnostics.Stopwatch]::StartNew()
    $cached=$session.RenderAsync($request,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $cacheWatch.Stop()
    if($cached.Samples.Length -ne $audio.Samples.Length){throw 'Real Vocaloid cached PCM length changed.'}
    for($i=0;$i -lt $audio.Samples.Length;$i++){if($cached.Samples[$i] -ne $audio.Samples[$i]){throw 'Real Vocaloid cached PCM changed.'}}
    foreach($note in $request.Notes){$note.Tone+=2}
    for($i=0;$i -lt $request.Pitch.Values.Length;$i++){$request.Pitch.Values[$i]+=2}
    $editWatch=[Diagnostics.Stopwatch]::StartNew()
    $edited=$session.RenderAsync($request,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $editWatch.Stop()
    $same=$edited.Samples.Length -eq $audio.Samples.Length
    if($same){for($i=0;$i -lt $audio.Samples.Length;$i++){if($edited.Samples[$i] -ne $audio.Samples[$i]){$same=$false;break}}}
    if($same){throw 'Changed notes/pitch produced identical native Vocaloid PCM.'}
    $phraseSeconds=$request.Notes[-1].EndSeconds-$request.Notes[0].StartSeconds
    $consecutiveTimes=@($editWatch.Elapsed.TotalSeconds)
    for($round=1;$round -lt $EditCount;$round++) {
        foreach($note in $request.Notes){$note.Tone+=1}
        for($i=0;$i -lt $request.Pitch.Values.Length;$i++){$request.Pitch.Values[$i]+=1}
        $roundWatch=[Diagnostics.Stopwatch]::StartNew()
        $again=$session.RenderAsync($request,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $roundWatch.Stop()
        if($again.Samples.Length -eq 0 -or $again.SampleRate -ne 44100){throw 'A repeated native render returned invalid audio.'}
        $roundPeak=0.0
        foreach($s in $again.Samples){$roundPeak=[Math]::Max($roundPeak,[Math]::Abs($s))}
        if($roundPeak -lt 0.0001){throw "Repeated native render $round returned silent audio."}
        $consecutiveTimes+=,$roundWatch.Elapsed.TotalSeconds
    }
    [ordered]@{
        status='real-vocaloid-render-verified'; voice_id=$settings.VoiceId; sample_rate=$audio.SampleRate
        sample_count=$audio.Samples.Length; peak=$peak; start_seconds=$audio.StartSeconds
        wav_path=$(if([string]::IsNullOrWhiteSpace($OutputWavPath)){$null}else{[IO.Path]::GetFullPath($OutputWavPath)})
        phrase_seconds=$phraseSeconds; host_startup_seconds=$cold.Elapsed.TotalSeconds
        first_render_seconds=$firstWatch.Elapsed.TotalSeconds; cached_render_seconds=$cacheWatch.Elapsed.TotalSeconds
        edited_render_seconds=$editWatch.Elapsed.TotalSeconds; edited_realtime_factor=($editWatch.Elapsed.TotalSeconds/$phraseSeconds)
        edited_faster_than_playback=($editWatch.Elapsed.TotalSeconds -lt $phraseSeconds)
        slowest_consecutive_render_seconds=(($consecutiveTimes | Measure-Object -Maximum).Maximum)
        all_consecutive_faster_than_playback=([bool](($consecutiveTimes | Where-Object {$_ -ge $phraseSeconds}).Count -eq 0))
        consecutive_native_renders=($EditCount+1)
    } | ConvertTo-Json
} finally {$session.Dispose()}
