param([string]$UnityEditor = 'C:\Program Files\Unity\Hub\Editor\6000.2.6f2\Editor\Unity.exe')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$missingCount = 0
function Check-Input([string]$Label, [string]$Path, [string]$Kind = 'Leaf') {
    if (Test-Path -LiteralPath $Path -PathType $Kind) {
        Write-Output "OK       $Label"
    } else {
        Write-Output "MISSING  $Label ($Path)"
        $script:missingCount++
    }
}
Check-Input 'Unity 6000.2.6f2 executable (verify version in Hub)' $UnityEditor
Check-Input 'Node runtime for packaging' 'C:\Program Files\nodejs\node.exe'
Check-Input 'User-provided VRM' (Join-Path $projectRoot 'AvatarAssets\hatsune-miku.vrm')
Check-Input 'Pose data' (Join-Path $projectRoot 'AvatarAssets\MikuPoses.json')
Check-Input 'Harness dependencies (run npm ci)' (Join-Path $projectRoot 'Tools\MikuHarness\node_modules\@deepseek-ai\dsh-sdk-client\package.json')
Check-Input 'Node license' (Join-Path $projectRoot 'Tools\MikuHarness\NODE-LICENSE.txt')
Check-Input 'Main scene' (Join-Path $projectRoot 'Assets\MATE ENGINE - Scenes\Mate Engine Main.unity')
Check-Input 'Animator controller' (Join-Path $projectRoot 'Assets\MATE ENGINE - Animations\AvatarAnimatorControllerV2 1.controller')
Check-Input 'Cursor flight motion' (Join-Path $projectRoot 'Assets\noirunn\KawaiiMacaronMotion\KawaiiMacaronMotion02.anim')
$motionNames = @('idle', 'walking', 'running', 'left turn', 'right turn', 'left strafe walk', 'right strafe walk', 'left strafe', 'right strafe', 'jump')
foreach ($motionName in $motionNames) {
    Check-Input "Humanoid motion: $motionName" (Join-Path $projectRoot "Assets\MaoIntegration\Motions\FemaleLocomotion\$motionName.fbx")
}
Write-Output ''
Write-Output 'Checks file presence only. Unity must validate rigs, compile and build.'
Write-Output 'See docs/INSTALL_VI.md and docs/ASSETS.md. No credentials were read.'
if ($missingCount -gt 0) {
    Write-Output "Missing required inputs: $missingCount"
    exit 1
}
Write-Output 'Required paths exist; continue with Unity validation.'
exit 0
