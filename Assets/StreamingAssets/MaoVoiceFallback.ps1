$ErrorActionPreference = 'Stop'
$text = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($text)) { exit 0 }

Add-Type -AssemblyName System.Speech
$speaker = [System.Speech.Synthesis.SpeechSynthesizer]::new()
try {
    $speaker.SetOutputToDefaultAudioDevice()
    try { $speaker.SelectVoice('Microsoft Zira Desktop') } catch {
        try {
            $speaker.SelectVoiceByHints(
                [System.Speech.Synthesis.VoiceGender]::Female,
                [System.Speech.Synthesis.VoiceAge]::Teen,
                0,
                [System.Globalization.CultureInfo]::GetCultureInfo('en-US'))
        } catch { }
    }
    $speaker.Rate = 2
    $speaker.Volume = 100
    $speaker.Speak($text)
}
finally {
    $speaker.Dispose()
}
