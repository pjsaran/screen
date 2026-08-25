<#
.SYNOPSIS
    Generates Captr's application and tray icons as multi-resolution .ico files.

.DESCRIPTION
    The icons are GENERATED, not hand-drawn binaries, so the design lives in
    reviewable code and can be adjusted without a graphics tool. Run this after
    changing the design; the resulting .ico files are committed.

    Design brief, and why it is the way it is:

      * App icon — a rounded "display" with a record dot. Reads as a screen
        recorder at 16 px and still looks deliberate at 256 px.

      * Tray icons must be legible on BOTH a dark and a light taskbar. Windows
        does not tint tray icons, so a single-colour glyph always loses on one of
        them. Every tray icon here is therefore drawn as a bright shape with a
        dark contrasting rim: the bright fill carries it on dark taskbars, the rim
        carries it on light ones.

      * Tray icons are drawn EDGE TO EDGE (only ~1 px of padding). The previous
        text-glyph icons looked tiny because glyph metrics left large margins;
        filling the square is what makes a 16 px icon feel full size.

      * Every tray icon is the SAME rounded display in Captr's accent blue, with
        only the "screen" inside it changing colour. That is what makes the set
        look like one product in five states rather than five unrelated glyphs.

      * Recording additionally BLINKS, alternating the bright red screen with a
        dimmed one. A static icon is calmer, but "am I actually recording?" is the
        one question the tray exists to answer, and movement is what answers it
        without being looked at directly. Only the SCREEN dims - the silhouette
        stays put, so the icon pulses rather than flickers.

      * States must be unmistakable at a glance (SPEC §9): idle is a pale screen,
        recording a red one, paused amber with the pause bars cut into it, and
        error red with an exclamation cut into it. Colour alone never carries the
        meaning — recording and error are both red, and the cut-out glyph is what
        separates them for anyone who cannot tell those reds apart.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

$outputDirectory = Join-Path $RepoRoot 'src\Captr.App\Assets'
New-Item -ItemType Directory -Force $outputDirectory | Out-Null

# ---- Palette -------------------------------------------------------------------
$rim        = [System.Drawing.Color]::FromArgb(255, 12, 16, 22)    # near-black outline
$screenFill = [System.Drawing.Color]::FromArgb(255, 236, 240, 245) # bright neutral
$accent     = [System.Drawing.Color]::FromArgb(255, 0, 120, 212)   # Windows blue
$recordRed  = [System.Drawing.Color]::FromArgb(255, 232, 17, 35)
# The blink's off-phase: the same red darkened rather than faded, so the icon reads
# as one shape pulsing instead of two different icons swapping.
$recordDim  = [System.Drawing.Color]::FromArgb(255, 116, 20, 28)
$pauseAmber = [System.Drawing.Color]::FromArgb(255, 255, 185, 0)
$idleGrey   = [System.Drawing.Color]::FromArgb(255, 176, 184, 194)

function New-Surface([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.Clear([System.Drawing.Color]::Transparent)
    return @{ Bitmap = $bitmap; Graphics = $graphics }
}

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

# ---- Drawings ------------------------------------------------------------------

# App icon: a rounded display in the accent colour with a red record dot.
function Draw-AppIcon($surface, [int]$size) {
    $g = $surface.Graphics
    $inset = [single]($size * 0.055)
    $side = [single]($size - ($inset * 2))
    $path = New-RoundedPath $inset $inset $side $side ([single]($size * 0.22))

    $g.FillPath((New-Object System.Drawing.SolidBrush $accent), $path)
    $rimPen = New-Object System.Drawing.Pen $rim, ([single][Math]::Max(1.0, $size * 0.05))
    $g.DrawPath($rimPen, $path)

    # The "screen" inside the frame.
    $screenInset = [single]($size * 0.24)
    $screenSide = [single]($size - ($screenInset * 2))
    $screenPath = New-RoundedPath $screenInset $screenInset $screenSide $screenSide ([single]($size * 0.08))
    $g.FillPath((New-Object System.Drawing.SolidBrush $screenFill), $screenPath)

    # Record dot, centred.
    $dot = [single]($size * 0.30)
    $dotOffset = [single](($size - $dot) / 2)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $recordRed), $dotOffset, $dotOffset, $dot, $dot)

    $path.Dispose(); $screenPath.Dispose(); $rimPen.Dispose()
}

# ---- Tray icons ----------------------------------------------------------------
# All four share one silhouette: Captr's rounded display, drawn edge to edge with a
# near-black rim. The rim is what keeps it visible on a LIGHT taskbar; the bright
# screen inside is what keeps it visible on a DARK one. Only the screen's colour and
# the glyph cut into it change between states.

# Draws the frame and returns the rectangle of the screen inside it, so each state
# only has to fill that rectangle and cut its glyph out.
function Draw-TrayFrame($surface, [int]$size, $screenColor) {
    $g = $surface.Graphics
    $inset = [single]([Math]::Max(1.0, $size * 0.045))
    $side = [single]($size - ($inset * 2))
    $framePath = New-RoundedPath $inset $inset $side $side ([single]($size * 0.22))

    $g.FillPath((New-Object System.Drawing.SolidBrush $accent), $framePath)
    $rimPen = New-Object System.Drawing.Pen $rim, ([single][Math]::Max(1.0, $size * 0.06))
    $g.DrawPath($rimPen, $framePath)

    $screenInset = [single]($size * 0.235)
    $screenSide = [single]($size - ($screenInset * 2))
    $screenPath = New-RoundedPath $screenInset $screenInset $screenSide $screenSide ([single]([Math]::Max(1.0, $size * 0.07)))
    $g.FillPath((New-Object System.Drawing.SolidBrush $screenColor), $screenPath)

    $framePath.Dispose(); $screenPath.Dispose(); $rimPen.Dispose()
    return @{ X = $screenInset; Y = $screenInset; Side = $screenSide }
}

# Idle: a pale, quiet screen. Nothing is being captured.
function Draw-TrayIdle($surface, [int]$size) {
    Draw-TrayFrame $surface $size $idleGrey | Out-Null
}

# Recording: the screen is red. TrayPresenter alternates this with the dim variant
# below to produce the blink.
function Draw-TrayRecording($surface, [int]$size) {
    Draw-TrayFrame $surface $size $recordRed | Out-Null
}

# The blink's off-phase.
function Draw-TrayRecordingDim($surface, [int]$size) {
    Draw-TrayFrame $surface $size $recordDim | Out-Null
}

# Paused: an amber screen with the two pause bars cut out of it in the rim colour,
# so the state is readable without relying on telling amber from red.
function Draw-TrayPaused($surface, [int]$size) {
    $screen = Draw-TrayFrame $surface $size $pauseAmber
    $g = $surface.Graphics

    $barWidth = [single]([Math]::Max(1.0, $screen.Side * 0.26))
    $gap = [single]([Math]::Max(1.0, $screen.Side * 0.18))
    $barHeight = [single]($screen.Side * 0.68)
    $top = [single]($screen.Y + (($screen.Side - $barHeight) / 2))
    $left = [single]($screen.X + (($screen.Side - (($barWidth * 2) + $gap)) / 2))

    $brush = New-Object System.Drawing.SolidBrush $rim
    $g.FillRectangle($brush, $left, $top, $barWidth, $barHeight)
    $g.FillRectangle($brush, [single]($left + $barWidth + $gap), $top, $barWidth, $barHeight)
}

# Error: a red screen with an exclamation cut out of it. Recording is also red, so
# the glyph — not the colour — is what says "this one needs you".
function Draw-TrayError($surface, [int]$size) {
    $screen = Draw-TrayFrame $surface $size $recordRed
    $g = $surface.Graphics

    $strokeWidth = [single]([Math]::Max(1.0, $screen.Side * 0.22))
    $x = [single]($screen.X + (($screen.Side - $strokeWidth) / 2))
    $stemHeight = [single]($screen.Side * 0.46)
    $stemTop = [single]($screen.Y + ($screen.Side * 0.14))

    $brush = New-Object System.Drawing.SolidBrush $rim
    $g.FillRectangle($brush, $x, $stemTop, $strokeWidth, $stemHeight)
    $g.FillRectangle($brush, $x, [single]($stemTop + $stemHeight + ($screen.Side * 0.10)), $strokeWidth, $strokeWidth)
}

# ---- ICO assembly ---------------------------------------------------------------
# Vista+ .ico files may hold PNG payloads, which keeps large sizes small and the
# writer simple: header, one directory entry per size, then the PNG bytes.
function Write-Ico([string]$path, [scriptblock]$draw, [int[]]$sizes) {
    $pngs = @()
    foreach ($size in $sizes) {
        $surface = New-Surface $size
        & $draw $surface $size
        $stream = New-Object System.IO.MemoryStream
        $surface.Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $surface.Graphics.Dispose(); $surface.Bitmap.Dispose()
        $pngs += , @{ Size = $size; Bytes = $stream.ToArray() }
        $stream.Dispose()
    }

    $output = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $output
    $writer.Write([uint16]0)                 # reserved
    $writer.Write([uint16]1)                 # type: icon
    $writer.Write([uint16]$pngs.Count)

    $offset = 6 + (16 * $pngs.Count)
    foreach ($png in $pngs) {
        $writer.Write([byte]($(if ($png.Size -ge 256) { 0 } else { $png.Size })))  # width (0 = 256)
        $writer.Write([byte]($(if ($png.Size -ge 256) { 0 } else { $png.Size })))  # height
        $writer.Write([byte]0)               # palette count
        $writer.Write([byte]0)               # reserved
        $writer.Write([uint16]1)             # colour planes
        $writer.Write([uint16]32)            # bits per pixel
        $writer.Write([uint32]$png.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $png.Bytes.Length
    }

    foreach ($png in $pngs) {
        $writer.Write($png.Bytes)
    }

    $writer.Flush()
    [System.IO.File]::WriteAllBytes($path, $output.ToArray())
    $writer.Dispose(); $output.Dispose()
    Write-Host ("  {0}  ({1:N0} bytes, {2} sizes)" -f (Split-Path $path -Leaf), (Get-Item $path).Length, $pngs.Count)
}

Write-Host "Generating icons into $outputDirectory"

# The application icon needs every size Explorer, the title bar, Alt-Tab, and
# Programs and Features ask for.
Write-Ico (Join-Path $outputDirectory 'captr.ico') ${function:Draw-AppIcon} @(16, 20, 24, 32, 40, 48, 64, 128, 256)

# Tray icons only ever render at notification-area sizes.
Write-Ico (Join-Path $outputDirectory 'tray-idle.ico')      ${function:Draw-TrayIdle}      @(16, 20, 24, 32, 48)
Write-Ico (Join-Path $outputDirectory 'tray-recording.ico') ${function:Draw-TrayRecording} @(16, 20, 24, 32, 48)
Write-Ico (Join-Path $outputDirectory 'tray-recording-dim.ico') ${function:Draw-TrayRecordingDim} @(16, 20, 24, 32, 48)
Write-Ico (Join-Path $outputDirectory 'tray-paused.ico')    ${function:Draw-TrayPaused}    @(16, 20, 24, 32, 48)
Write-Ico (Join-Path $outputDirectory 'tray-error.ico')     ${function:Draw-TrayError}     @(16, 20, 24, 32, 48)

Write-Host "Done. These files are committed; re-run this script only when the design changes."
