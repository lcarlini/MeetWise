# Captures every picture used by docs\index.html, in English and Portuguese, from the real app (run a Release build first).
#
#   powershell -ExecutionPolicy Bypass -File tools\capture-docs.ps1 [-Languages en,pt] [-Quick]
#
# Needs the benchmark clip (MeetWise.App.exe --benchmark --quick creates it) and a demo data folder seeded as described in capture-screens.ps1.
# -Quick skips the three tests that load the AI language model (translation, coach, assistant).
param([string[]]$Languages = @("en", "pt"), [switch]$Quick)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$img = Join-Path $here "..\docs\img"
$Names = @{
  en = @{ Sfx = ""; Record = "Record"; Meetings = "Meetings"; Assistant = "AI Assistant"; Voice = "Voice lock: pass only my voice"; Train = "Train my voice…";
          Cleanup = "Microphone clean-up"; Transcribe = "Subtitles: live"; Speakers = "Who is speaking"; Translate = "Translation"; Coach = "English coach"; Sample = "AI assistant"; Enc = "Video encoders";
          TrText = "Please send me the budget before Friday's meeting."; TrLang = "Portuguese";
          CoachText = "Yesterday I go to the office and we discussed about the budget." }
  pt = @{ Sfx = "-pt"; Record = "Gravar"; Meetings = "Reuniões"; Assistant = "Assistente de IA"; Voice = "Trava de voz: só a minha voz"; Train = "Treinar minha voz…";
          Cleanup = "Limpeza do microfone"; Transcribe = "Legendas: motor"; Speakers = "Quem está falando"; Translate = "Tradução"; Coach = "Treinador de inglês"; Sample = "Assistente de IA em"; Enc = "Codificadores";
          TrText = "Por favor, me envie o orçamento antes da reunião de sexta-feira."; TrLang = "Inglês";
          CoachText = "Yesterday I go to the office and we discussed about the budget." }
}
foreach ($lang in $Languages) {
    $t = $Names[$lang]; $s = $t.Sfx
    & (Join-Path $here "capture-screens.ps1") -Language $lang -Suffix $s
    function Drive { param([hashtable]$A) & (Join-Path $here "try-drive.ps1") -Language $lang @A }
    Drive @{ Page = $t.Meetings; DialogButton = $t.Train; Shot = (Join-Path $img "voice-training$s.png") }
    Drive @{ Page = $t.Meetings; HoverName = $t.Voice; Shot = (Join-Path $img "tooltip$s.png") }
    Drive @{ Page = $t.Meetings; Test = $t.Cleanup; Shot = (Join-Path $img "try-cleanup$s.png") }
    Drive @{ Page = $t.Meetings; Test = $t.Speakers; Shot = (Join-Path $img "try-speakers$s.png") }
    Drive @{ Page = $t.Record; Test = $t.Transcribe; Shot = (Join-Path $img "try-transcribe$s.png") }
    Drive @{ Page = $t.Record; Test = $t.Enc; Shot = (Join-Path $img "try-encoders$s.png") }
    if (-not $Quick) {
        Drive @{ Page = $t.Meetings; Test = $t.Translate; TypeText = $t.TrText; LanguageChoice = $t.TrLang; Shot = (Join-Path $img "try-translate$s.png") }
        Drive @{ Page = $t.Meetings; Test = $t.Coach; TypeText = $t.CoachText; Shot = (Join-Path $img "try-coach$s.png") }
        Drive @{ Page = $t.Assistant; Test = $t.Sample; Shot = (Join-Path $img "try-assistant$s.png") }
    }
}
"done"
