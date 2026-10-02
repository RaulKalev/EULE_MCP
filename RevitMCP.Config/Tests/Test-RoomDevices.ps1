<#
.SYNOPSIS
    Live Revit tests for the room geometry, device placement, device audit, fire alarm and cable
    type tools (issues #51 and #52).

.DESCRIPTION
    Talks to the RevitMCP add-in directly over its named pipe (the same newline-delimited
    McpToolRequest JSON the bridge sends), so it needs no MCP client and works right after a deploy.
    Every read-only tool and every preview is run against the open model and its result is checked.
    Device codes are passed inline, so the project config is not touched.

    With -IncludeWrites the script also sends the write tools. Each one stops at the connector's
    approval prompt: approve it in Revit, press Enter here, and the script verifies the result with
    read tools. Undo the changes in Revit afterwards (or close without saving).

    Matching manual checklist: TESTING.md sections 53-56.

.EXAMPLE
    .\Test-RoomDevices.ps1 -LinkInstanceId 123456 -RoomNumber "1.12" -CorridorNumber "1.01"

.EXAMPLE
    .\Test-RoomDevices.ps1 -Source host -RoomNumber "101" -IncludeWrites -RoomParameter "Ruum"
#>
[CmdletBinding()]
param(
    # Room source: link (AR link, default) or host.
    [ValidateSet("link", "host")] [string] $Source = "link",
    # AR link instance id; 0 = the only loaded link.
    [long] $LinkInstanceId = 0,
    # A normal room, ideally with a door and a window.
    [Parameter(Mandatory = $true)] [string] $RoomNumber,
    # Optional corridor (<= 2 m wide) for the corridor rule test.
    [string] $CorridorNumber = "",
    [string] $LevelName = "",
    # Family types for the inline test codes. Empty = picked automatically from the model.
    [string] $WallFamily = "", [string] $WallType = "",
    [string] $CeilingFamily = "", [string] $CeilingType = "",
    # Cable type to duplicate in the cable type tests (empty = the first cable type).
    [string] $CableSourceType = "",
    # Text instance parameter for the assign-room test (skipped when empty).
    [string] $RoomParameter = "",
    # Also send write tools (approve each in Revit, then Undo afterwards).
    [switch] $IncludeWrites,
    [int] $TimeoutSec = 180
)

$ErrorActionPreference = "Stop"
$script:Results = New-Object System.Collections.Generic.List[object]
$script:Context = @{}

# -- Pipe client --------------------------------------------------------------

function Get-RevitPipeName {
    $dir = Join-Path $env:LOCALAPPDATA "RevitMCP\Instances"
    if (-not (Test-Path $dir)) { throw "No RevitMCP instance registry at $dir - is Revit running with the connector started?" }
    $live = @()
    foreach ($file in Get-ChildItem $dir -Filter "instance-*.json") {
        try {
            $info = Get-Content $file.FullName -Raw | ConvertFrom-Json
            if (Get-Process -Id $info.ProcessId -ErrorAction SilentlyContinue) { $live += $info }
        } catch { }
    }
    if ($live.Count -eq 0) { throw "No live Revit instance registered - start the MCP connector in Revit." }
    $pick = $live | Sort-Object { [datetime]$_.UpdatedUtc } -Descending | Select-Object -First 1
    Write-Host ("Revit {0} (pid {1}) - {2}" -f $pick.RevitVersion, $pick.ProcessId, $pick.DocumentTitle) -ForegroundColor Cyan
    return $pick.PipeName
}

function Invoke-RevitTool([string] $Tool, [hashtable] $Arguments = @{}) {
    $request = @{
        RequestId  = [guid]::NewGuid().ToString()
        ToolName   = $Tool
        Arguments  = $Arguments
        ClientName = "Test-RoomDevices"
        CreatedAt  = (Get-Date).ToString("o")
    }
    $json = $request | ConvertTo-Json -Depth 20 -Compress

    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", $script:PipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(10000)
        $writer = New-Object System.IO.StreamWriter($pipe)
        $writer.AutoFlush = $true
        $reader = New-Object System.IO.StreamReader($pipe)
        $writer.WriteLine($json)
        $task = $reader.ReadLineAsync()
        if (-not $task.Wait($TimeoutSec * 1000)) { throw "Timed out after $TimeoutSec s waiting for $Tool." }
        if ($null -eq $task.Result) { throw "Connector closed the pipe without a result for $Tool." }
        return $task.Result | ConvertFrom-Json
    } finally {
        $pipe.Dispose()
    }
}

# -- Test harness -------------------------------------------------------------

function Test-Case([string] $Id, [string] $Name, [scriptblock] $Body) {
    try {
        $outcome = & $Body
        if ($outcome -is [string] -and $outcome.StartsWith("SKIP")) {
            $script:Results.Add([pscustomobject]@{ Id = $Id; Name = $Name; Result = "SKIP"; Detail = $outcome.Substring(4).Trim(": ") })
            Write-Host ("  SKIP {0} {1} - {2}" -f $Id, $Name, $outcome.Substring(4).Trim(": ")) -ForegroundColor DarkYellow
        } else {
            $script:Results.Add([pscustomobject]@{ Id = $Id; Name = $Name; Result = "PASS"; Detail = [string]$outcome })
            Write-Host ("  PASS {0} {1} {2}" -f $Id, $Name, $outcome) -ForegroundColor Green
        }
    } catch {
        $script:Results.Add([pscustomobject]@{ Id = $Id; Name = $Name; Result = "FAIL"; Detail = $_.Exception.Message })
        Write-Host ("  FAIL {0} {1} - {2}" -f $Id, $Name, $_.Exception.Message) -ForegroundColor Red
    }
}

function Assert([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }

function Assert-Ok($Result, [string] $Tool) {
    Assert ($null -ne $Result) "$Tool returned nothing."
    if (-not $Result.Success) {
        $w = ""
        if ($Result.Warnings) { $w = " Warnings: " + (($Result.Warnings | Select-Object -First 3) -join " | ") }
        throw "$Tool failed: $($Result.Message)$w"
    }
}

function Assert-Approval($Result, [string] $Tool) {
    Assert ($null -ne $Result) "$Tool returned nothing."
    if ($Result.Success -and $Result.Status -ne "approval_required") { return "applied directly (Direct Edit on)" }
    Assert ($Result.Status -eq "approval_required") "$Tool expected approval_required, got status '$($Result.Status)': $($Result.Message)"
    Read-Host "    -> Approve '$Tool' in the Revit connector window, then press Enter" | Out-Null
    return "approved"
}

# -- Geometry helpers (host mm) ------------------------------------------------

function Get-SignedArea($Points) {
    $sum = 0.0
    for ($i = 0; $i -lt $Points.Count; $i++) {
        $a = $Points[$i]; $b = $Points[($i + 1) % $Points.Count]
        $sum += $a.x * $b.y - $b.x * $a.y
    }
    return $sum / 2
}

function Test-InLoop($Points, [double] $X, [double] $Y) {
    $inside = $false
    $j = $Points.Count - 1
    for ($i = 0; $i -lt $Points.Count; $i++) {
        $a = $Points[$i]; $b = $Points[$j]
        if ((($a.y -gt $Y) -ne ($b.y -gt $Y)) -and ($X -lt ($b.x - $a.x) * ($Y - $a.y) / ($b.y - $a.y) + $a.x)) { $inside = -not $inside }
        $j = $i
    }
    return $inside
}

function Test-InRoom($Room, [double] $X, [double] $Y) {
    if (-not (Test-InLoop $Room.boundary.outer $X $Y)) { return $false }
    foreach ($hole in $Room.boundary.holes) { if (Test-InLoop $hole $X $Y) { return $false } }
    return $true
}

function Get-AngleDeg([double] $X, [double] $Y) {
    $d = [math]::Atan2($Y, $X) * 180 / [math]::PI
    if ($d -lt 0) { $d += 360 }
    return $d
}

function Get-AngleDiff([double] $A, [double] $B) {
    $d = [math]::Abs(($A - $B) % 360)
    if ($d -gt 180) { $d = 360 - $d }
    return $d
}

function New-RoomArgs([hashtable] $Extra = @{}) {
    $a = @{ source = $Source; linkInstanceId = $LinkInstanceId; levelName = $LevelName; projectRoot = "" }
    foreach ($k in $Extra.Keys) { $a[$k] = $Extra[$k] }
    return $a
}

function Get-InlineCodes {
    $wall = @{ family = $script:Context.WallFamily; type = $script:Context.WallType; mount = "wall"; heightMm = 300; offsetFromWallMm = 0; doorSide = "lock"; doorOffsetMm = 150 }
    return @{
        T_WALL   = $wall
        T_SA     = @{ family = $script:Context.CeilingFamily; type = $script:Context.CeilingType; mount = "ceiling"; offsetFromCeilingMm = 0; detectorType = "pointSmoke"; avoidCategories = @("Lighting Fixtures") }
        T_TA_A2  = @{ family = $script:Context.CeilingFamily; type = $script:Context.CeilingType; mount = "ceiling"; detectorType = "pointHeat"; detectorClass = "A2" }
        T_FREE   = @{ family = $script:Context.CeilingFamily; type = $script:Context.CeilingType; mount = "ceiling"; maxSpacingMm = 3000 }
        T_SIR    = @{ family = $script:Context.WallFamily; type = $script:Context.WallType; mount = "wall"; heightMm = 2400; detectorType = "sounder"; soundLevelDb = 95; tone = "test-tone" }
        T_NEWTYP = @{ family = $script:Context.WallFamily; type = ($script:Context.WallType + " MCPTEST"); mount = "wall"; heightMm = 300; sourceType = $script:Context.WallType }
    }
}

# -- Run ----------------------------------------------------------------------

$script:PipeName = Get-RevitPipeName
Write-Host ""

Write-Host "Section 54 - room geometry" -ForegroundColor Cyan

Test-Case "54.1" "connection status" {
    $r = Invoke-RevitTool "revit_get_connection_status"
    Assert-Ok $r "revit_get_connection_status"
    "doc: $($r.Data.documentTitle)$($r.Data.DocumentTitle)"
}

Test-Case "54.2" "list levels (+ link levels mapped)" {
    $r = Invoke-RevitTool "revit_list_levels" @{ linkInstanceId = $LinkInstanceId }
    Assert-Ok $r "revit_list_levels"
    Assert ($r.Data.hostLevels.Count -gt 0) "No host levels."
    $script:Context.HostLevels = $r.Data.hostLevels
    if ($Source -eq "link" -and $LinkInstanceId -ne 0) {
        Assert ($r.Data.linkLevels.Count -gt 0) "Link has no levels."
        Assert (@($r.Data.linkLevels | Where-Object { $_.hostLevelName }).Count -eq $r.Data.linkLevels.Count) "Some link levels are not mapped to a host level."
        return "$($r.Data.hostLevels.Count) host, $($r.Data.linkLevels.Count) link levels"
    }
    "$($r.Data.hostLevels.Count) host levels"
}

Test-Case "54.3" "room geometry: boundary, interior point, doors" {
    $r = Invoke-RevitTool "revit_get_room_geometry" (New-RoomArgs @{ roomNumbers = @($RoomNumber) })
    Assert-Ok $r "revit_get_room_geometry"
    Assert ($r.Data.rooms.Count -ge 1) "Room $RoomNumber not found. Warnings: $($r.Warnings -join ' | ')"
    $room = $r.Data.rooms[0]
    $script:Context.Room = $room
    Assert ($room.boundary.outer.Count -ge 3) "Outer boundary has fewer than 3 points."
    Assert ((Get-SignedArea $room.boundary.outer) -lt 0) "Outer boundary is not clockwise."
    Assert ($room.areaM2 -gt 0) "areaM2 is not positive."
    Assert (Test-InRoom $room $room.interiorPoint.x $room.interiorPoint.y) "interiorPoint is outside the room."
    Assert ($null -ne $room.level) "Room has no host level."
    Assert (@($script:Context.HostLevels | Where-Object { $_.name -eq $room.level }).Count -eq 1) "Room level '$($room.level)' is not a host level."
    "$($room.kind) '$($room.name)', $($room.areaM2) m2, $(@($room.doors).Count) door(s), $(@($room.windows).Count) window(s), ceiling $($room.ceilingHeightMm) mm"
}

Test-Case "54.4" "doors carry wall, along range and sides" {
    $room = $script:Context.Room
    Assert ($null -ne $room) "Needs 54.3."
    $doors = @($room.doors)
    if ($doors.Count -eq 0) { return "SKIP: room has no doors" }
    foreach ($d in $doors) {
        Assert ($d.widthMm -gt 300) "Door $($d.id) width $($d.widthMm) mm looks wrong."
        Assert ($null -ne $d.wallIndex) "Door $($d.id) is not assigned to a wall face."
        Assert ($d.alongToMm -gt $d.alongFromMm) "Door $($d.id) along range is empty."
    }
    $d0 = $doors[0]
    "door $($d0.id): wall $($d0.wallIndex), $($d0.alongFromMm)-$($d0.alongToMm) mm, lock $($d0.lockSide) ($($d0.sideSource)), swingIn $($d0.swingIntoRoom)"
}

Test-Case "54.5" "room walls: normals point into the room" {
    $room = $script:Context.Room
    Assert ($null -ne $room) "Needs 54.3."
    $r = Invoke-RevitTool "revit_get_room_walls" (New-RoomArgs @{ roomNumber = $RoomNumber })
    Assert-Ok $r "revit_get_room_walls"
    $walls = @($r.Data.walls)
    Assert ($walls.Count -ge 3) "Fewer than 3 wall faces."
    foreach ($w in $walls) {
        $len = [math]::Sqrt($w.normalIntoRoom.x * $w.normalIntoRoom.x + $w.normalIntoRoom.y * $w.normalIntoRoom.y)
        Assert ([math]::Abs($len - 1) -lt 0.001) "Face $($w.index) normal is not a unit vector."
        $mx = ($w.innerFace.start.x + $w.innerFace.end.x) / 2 + $w.normalIntoRoom.x * 100
        $my = ($w.innerFace.start.y + $w.innerFace.end.y) / 2 + $w.normalIntoRoom.y * 100
        Assert (Test-InRoom $room $mx $my) "Face $($w.index) normal points out of the room."
    }
    $script:Context.Walls = $walls
    $script:Context.WallIndex = ($walls | Sort-Object { - $_.lengthMm } | Select-Object -First 1).index
    $withWall = @($walls | Where-Object { $_.wallId }).Count
    "$($walls.Count) faces, $withWall with a bounding wall id, $(@($walls | Where-Object { $_.isSeparationLine }).Count) separation line(s)"
}

Test-Case "54.6" "linked elements in room (storey-aware)" {
    $r = Invoke-RevitTool "revit_get_linked_elements_in_room" (New-RoomArgs @{ roomNumber = $RoomNumber; categories = @("Ceilings", "Lighting Fixtures", "Furniture") })
    Assert-Ok $r "revit_get_linked_elements_in_room"
    $room = $script:Context.Room
    $elements = @($r.Data.rooms[0].elements)
    foreach ($e in $elements) {
        Assert ($e.bbox.maxZ -ge ($room.floorElevationMm - 100)) "Element $($e.id) is below the room's storey."
    }
    "$($elements.Count) element(s)"
}

Write-Host "Section 53 - cable types (#51)" -ForegroundColor Cyan

Test-Case "53.1" "cable types: preview create / skip existing / block invalid" {
    $types = Invoke-RevitTool "revit_get_available_cable_types"
    Assert-Ok $types "revit_get_available_cable_types"
    $list = @($types.Data.cableTypes)
    if ($list.Count -eq 0) { return "SKIP: model has no cable types" }
    $source = $CableSourceType
    if (-not $source) { $source = $list[0].name }
    $script:Context.CableSource = $source
    $script:Context.CableNew = "MCP test cable " + (Get-Date -Format "HHmmss")
    $r = Invoke-RevitTool "revit_preview_create_cable_type" @{ sourceTypeName = $source; items = @(@{ newName = $script:Context.CableNew }, @{ newName = $source }, @{ newName = "bad:name" }); ifExists = "skip" }
    Assert-Ok $r "revit_preview_create_cable_type"
    $p = @($r.Data.proposals)
    Assert ($p[0].action -eq "create") "New name should be 'create', got '$($p[0].action)'."
    Assert ($p[1].action -eq "skipExisting") "Existing name should be 'skipExisting', got '$($p[1].action)'."
    Assert ($p[2].action -eq "blocked") "Invalid name should be 'blocked', got '$($p[2].action)'."
    "$($r.Data.kind) from '$source'"
}

Write-Host "Section 54 - device codes and placement previews" -ForegroundColor Cyan

Test-Case "54.7" "pick family types for the test codes" {
    if ($WallFamily -and $WallType -and $CeilingFamily -and $CeilingType) {
        $script:Context.WallFamily = $WallFamily; $script:Context.WallType = $WallType
        $script:Context.CeilingFamily = $CeilingFamily; $script:Context.CeilingType = $CeilingType
        return "given"
    }
    $picked = $null
    foreach ($cat in @("Fire Alarm Devices", "Communication Devices", "Data Devices", "Electrical Fixtures", "Generic Models")) {
        $r = Invoke-RevitTool "revit_list_family_types" @{ category = $cat; includeSystemTypes = $false; limit = 5 }
        if ($r.Success -and @($r.Data.types).Count -gt 0) { $picked = $r.Data.types[0]; break }
    }
    Assert ($null -ne $picked) "No loadable family type found - pass -WallFamily/-WallType/-CeilingFamily/-CeilingType."
    if (-not $script:Context.WallFamily) { $script:Context.WallFamily = $picked.familyName; $script:Context.WallType = $picked.typeName }
    if (-not $script:Context.CeilingFamily) { $script:Context.CeilingFamily = $picked.familyName; $script:Context.CeilingType = $picked.typeName }
    "$($picked.familyName) : $($picked.typeName) ($($picked.category))"
}

Test-Case "54.8" "device codes validate against the model" {
    $r = Invoke-RevitTool "revit_get_device_codes" @{ deviceCodes = (Get-InlineCodes) }
    Assert-Ok $r "revit_get_device_codes"
    $codes = @($r.Data.codes)
    Assert ((@($codes | Where-Object { $_.code -eq "T_WALL" })[0].status) -eq "ok") "T_WALL type is not loaded."
    Assert ((@($codes | Where-Object { $_.code -eq "T_NEWTYP" })[0].status) -eq "missingType") "T_NEWTYP should be missingType (creatable)."
    "$($codes.Count) codes"
}

Test-Case "54.9" "ensure device types preview" {
    $r = Invoke-RevitTool "revit_preview_ensure_device_types" @{ deviceCodes = (Get-InlineCodes); codes = @("T_WALL", "T_NEWTYP") }
    Assert-Ok $r "revit_preview_ensure_device_types"
    $t = @($r.Data.types)
    Assert ((@($t | Where-Object { $_.code -eq "T_WALL" })[0].action) -eq "exists") "T_WALL should exist."
    Assert ((@($t | Where-Object { $_.code -eq "T_NEWTYP" })[0].action) -eq "create") "T_NEWTYP should be created."
    "ok"
}

Test-Case "54.10" "place_at_wall preview: point, height, facing" {
    $walls = $script:Context.Walls
    Assert ($null -ne $walls) "Needs 54.5."
    $face = @($walls | Where-Object { $_.index -eq $script:Context.WallIndex })[0]
    $r = Invoke-RevitTool "revit_preview_place_at_wall" (New-RoomArgs @{ deviceCodes = (Get-InlineCodes); placements = @(@{ deviceCode = "T_WALL"; roomNumber = $RoomNumber; wallIndex = $face.index; alongFraction = 0.5 }) })
    Assert-Ok $r "revit_preview_place_at_wall"
    $d = @($r.Data.devices)[0]
    Assert ($d.status -ne "blocked") "Device blocked: $($d.reason)"
    Assert ([math]::Abs($d.elevationFromLevelMm - 300) -lt 1) "Height above level is $($d.elevationFromLevelMm), expected 300."
    Assert ([math]::Abs($d.alongMm - $face.lengthMm / 2) -lt 2) "alongMm $($d.alongMm) is not the face midpoint."
    $normalDeg = Get-AngleDeg $face.normalIntoRoom.x $face.normalIntoRoom.y
    Assert ((Get-AngleDiff $d.facingDeg $normalDeg) -lt 0.5) "Facing $($d.facingDeg) deg does not match the wall normal $normalDeg deg."
    Assert (Test-InRoom $script:Context.Room ($d.point.x + $face.normalIntoRoom.x * 20) ($d.point.y + $face.normalIntoRoom.y * 20)) "Point is not on the room side of the wall."
    "face $($face.index), facing $($d.facingDeg) deg, warnings: $(@($d.warnings).Count)"
}

Test-Case "54.11" "place_at_wall count + spacing" {
    $face = @($script:Context.Walls | Where-Object { $_.index -eq $script:Context.WallIndex })[0]
    $r = Invoke-RevitTool "revit_preview_place_at_wall" (New-RoomArgs @{ deviceCodes = (Get-InlineCodes); placements = @(@{ deviceCode = "T_WALL"; roomNumber = $RoomNumber; wallIndex = $face.index; alongMm = 500; count = 3; spacingMm = 600 }) })
    Assert-Ok $r "revit_preview_place_at_wall"
    $d = @($r.Data.devices)
    Assert ($d.Count -eq 3) "Expected 3 devices, got $($d.Count)."
    Assert ([math]::Abs($d[1].alongMm - $d[0].alongMm - 600) -lt 1 -and [math]::Abs($d[2].alongMm - $d[1].alongMm - 600) -lt 1) "Spacing is not 600 mm."
    "along $($d[0].alongMm), $($d[1].alongMm), $($d[2].alongMm)"
}

Test-Case "54.12" "place_at_wall next to a door (lock side)" {
    $door = @($script:Context.Room.doors | Where-Object { $null -ne $_.wallIndex })[0]
    if ($null -eq $door) { return "SKIP: room has no door on a wall face" }
    $r = Invoke-RevitTool "revit_preview_place_at_wall" (New-RoomArgs @{ deviceCodes = (Get-InlineCodes); placements = @(@{ deviceCode = "T_WALL"; roomNumber = $RoomNumber; nearDoorId = $door.id; doorSide = "lock"; offsetMm = 150 }) })
    Assert-Ok $r "revit_preview_place_at_wall"
    $d = @($r.Data.devices)[0]
    Assert ($d.status -ne "blocked") "Blocked: $($d.reason)"
    $gap = [math]::Min([math]::Abs($d.alongMm - $door.alongToMm), [math]::Abs($door.alongFromMm - $d.alongMm))
    Assert ([math]::Abs($gap - 150) -lt 2) "Device is $gap mm from the door edge, expected 150."
    "along $($d.alongMm) (door $($door.alongFromMm)-$($door.alongToMm), lock $($door.lockSide)) - CHECK IN REVIT that this is the latch side"
}

Test-Case "54.13" "place_in_room center + points" {
    $r = Invoke-RevitTool "revit_preview_place_in_room" (New-RoomArgs @{ deviceCodes = (Get-InlineCodes); deviceCode = "T_FREE"; roomNumbers = @($RoomNumber); strategy = "center" })
    Assert-Ok $r "revit_preview_place_in_room"
    $d = @($r.Data.devices)[0]
    Assert (Test-InRoom $script:Context.Room $d.point.x $d.point.y) "Center point is outside the room."
    $p = Invoke-RevitTool "revit_preview_place_in_room" (New-RoomArgs @{ deviceCodes = (Get-InlineCodes); deviceCode = "T_FREE"; roomNumbers = @($RoomNumber); strategy = "points"; points = @(@{ u = 0.5; v = 0.5 }) })
    Assert-Ok $p "revit_preview_place_in_room"
    Assert (@($p.Data.devices).Count -eq 1) "points strategy should give 1 device."
    if ($null -ne $script:Context.Room.ceilingHeightMm) {
        Assert ([math]::Abs(($d.zMm - $script:Context.Room.floorElevationMm) - $script:Context.Room.ceilingHeightMm) -lt 1) "Ceiling device is not at the ceiling."
    }
    "center ($($d.point.x), $($d.point.y)), z above floor $([math]::Round($d.zMm - $script:Context.Room.floorElevationMm))"
}

Test-Case "54.14" "place_in_room grid (explicit spacing) stays inside" {
    $r = Invoke-RevitTool "revit_preview_place_in_room" (New-RoomArgs @{ deviceCodes = (Get-InlineCodes); deviceCode = "T_FREE"; roomNumbers = @($RoomNumber); strategy = "grid" })
    Assert-Ok $r "revit_preview_place_in_room"
    $d = @($r.Data.devices)
    Assert ($d.Count -ge 1) "Grid produced no points."
    foreach ($x in $d) { Assert (Test-InRoom $script:Context.Room $x.point.x $x.point.y) "Grid point ($($x.point.x), $($x.point.y)) is outside." }
    $note = @($r.Data.rooms)[0]
    Assert ($note.stepXMm -le 3000.5 -and $note.stepYMm -le 3000.5) "Grid step exceeds 3000 mm."
    "$($d.Count) point(s), step $($note.stepXMm) x $($note.stepYMm), uncovered $($note.uncoveredAreaM2) m2"
}

Write-Host "Section 56 - fire alarm rules" -ForegroundColor Cyan

Test-Case "56.1" "smoke grid follows 6.5.2.3 (8.8 m / 4.4 m)" {
    $r = Invoke-RevitTool "revit_preview_place_in_room" (New-RoomArgs @{ deviceCodes = (Get-InlineCodes); deviceCode = "T_SA"; roomNumbers = @($RoomNumber); strategy = "grid" })
    Assert-Ok $r "revit_preview_place_in_room"
    $note = @($r.Data.rooms)[0]
    if ($note.layout -eq "corridor") { return "room is a corridor: $($note.points) point(s) on the centreline" }
    Assert ($note.rule -eq "6.5.2.3") "Rule should be 6.5.2.3, got '$($note.rule)'."
    Assert ($note.stepXMm -le 8800.5 -and $note.stepYMm -le 8800.5) "Step exceeds 8.8 m."
    Assert ($note.coverageRadiusMm -eq 6200) "Coverage radius should be 6200, got $($note.coverageRadiusMm)."
    "$($note.points) detector(s), step $($note.stepXMm) x $($note.stepYMm)"
}

Test-Case "56.2" "corridor centreline layout" {
    if (-not $CorridorNumber) { return "SKIP: pass -CorridorNumber" }
    $r = Invoke-RevitTool "revit_preview_place_in_room" (New-RoomArgs @{ deviceCodes = (Get-InlineCodes); deviceCode = "T_SA"; roomNumbers = @($CorridorNumber); strategy = "grid" })
    Assert-Ok $r "revit_preview_place_in_room"
    $note = @($r.Data.rooms)[0]
    Assert ($note.layout -eq "corridor") "Not detected as a corridor (width $($note.widthMm) mm). Only rooms <= 2 m wide count."
    Assert ($note.maxSpacingMm -eq 12400) "Corridor spacing should be 12400."
    $pts = @($r.Data.devices)
    Assert ($pts.Count -eq [math]::Ceiling($note.lengthMm / 12400)) "Expected ceil(length/12.4 m) detectors."
    "$($pts.Count) detector(s) along $($note.lengthMm) mm"
}

Test-Case "56.3" "Table 1: point heat class A2 vs room height" {
    $room = $script:Context.Room
    $height = $room.ceilingHeightMm
    if ($null -eq $height) { $height = $room.heightMm }
    if ($null -eq $height) { return "SKIP: room height unknown" }
    $r = Invoke-RevitTool "revit_preview_place_in_room" (New-RoomArgs @{ deviceCodes = (Get-InlineCodes); deviceCode = "T_TA_A2"; roomNumbers = @($RoomNumber); strategy = "center" })
    Assert-Ok $r "revit_preview_place_in_room"
    $d = @($r.Data.devices)[0]
    if ($height -le 6000) {
        Assert ($d.status -ne "blocked") "A2 heat detector at $height mm should be allowed."
        "allowed at $height mm"
    } else {
        Assert ($d.status -eq "blocked") "A2 heat detector at $height mm should be blocked by Table 1."
        "blocked at $height mm: $($d.reason)"
    }
}

Test-Case "56.4" "check_fire_alarm runs and reports" {
    $r = Invoke-RevitTool "revit_check_fire_alarm" (New-RoomArgs @{ deviceCodes = (Get-InlineCodes); roomNumbers = @($RoomNumber); sleepingRoomFilter = "magamis" })
    Assert-Ok $r "revit_check_fire_alarm"
    $room = @($r.Data.rooms)[0]
    Assert ($null -ne $room) "No room in the report."
    "findings: " + ((@($room.findings) | ForEach-Object { $_.kind }) -join ", ")
}

Write-Host "Section 55 - audits and image" -ForegroundColor Cyan

Test-Case "55.1" "devices per room / alignment / coverage run" {
    $a = Invoke-RevitTool "revit_check_devices_per_room" (New-RoomArgs @{ deviceCodes = (Get-InlineCodes); rules = @(@{ code = "T_WALL"; roomNumbers = @($RoomNumber); min = 1 }) })
    Assert-Ok $a "revit_check_devices_per_room"
    $b = Invoke-RevitTool "revit_check_device_alignment" (New-RoomArgs @{ deviceCodes = (Get-InlineCodes) })
    Assert-Ok $b "revit_check_device_alignment"
    $c = Invoke-RevitTool "revit_check_coverage" (New-RoomArgs @{ deviceCodes = (Get-InlineCodes); codes = @("T_SA"); roomNumbers = @($RoomNumber) })
    Assert-Ok $c "revit_check_coverage"
    "rule findings $(@($a.Data.findings).Count), misaligned $(@($b.Data.issues).Count), coverage $(@($c.Data.rooms)[0].coveredPercent)%"
}

Test-Case "55.2" "export view image cropped to the room" {
    $r = Invoke-RevitTool "revit_export_view_image" (New-RoomArgs @{ cropToRoom = $RoomNumber; pixelSize = 1200 })
    Assert-Ok $r "revit_export_view_image"
    $path = $r.Data.imagePath
    Assert (Test-Path $path) "Image file $path does not exist."
    $bytes = [IO.File]::ReadAllBytes($path)
    Assert ($bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50 -and $bytes[2] -eq 0x4E -and $bytes[3] -eq 0x47) "File is not a PNG."
    $script:Context.ImagePath = $path
    "$path ($([math]::Round($bytes.Length / 1kb)) kB)$(if ($r.Warnings) { ' - ' + ($r.Warnings -join ' | ') })"
}

# -- Writes -------------------------------------------------------------------

if ($IncludeWrites) {
    Write-Host "Writes - approve each one in Revit; Undo everything afterwards" -ForegroundColor Magenta

    Test-Case "W.1" "place_at_wall apply, then audit sees it aligned" {
        $face = @($script:Context.Walls | Where-Object { $_.index -eq $script:Context.WallIndex })[0]
        $placement = @{ deviceCode = "T_WALL"; roomNumber = $RoomNumber; wallIndex = $face.index; alongFraction = 0.5 }
        $r = Invoke-RevitTool "revit_place_at_wall" (New-RoomArgs @{ deviceCodes = (Get-InlineCodes); placements = @($placement); roomParameter = $RoomParameter })
        $how = Assert-Approval $r "revit_place_at_wall"
        $again = Invoke-RevitTool "revit_preview_place_at_wall" (New-RoomArgs @{ deviceCodes = (Get-InlineCodes); placements = @($placement) })
        Assert-Ok $again "revit_preview_place_at_wall"
        Assert ((@($again.Data.devices)[0].warnings -join " ") -match "existing") "The placed device is not found (no duplicate warning on re-preview)."
        # A negative tolerance lists every device with its measured offsets.
        $all = Invoke-RevitTool "revit_check_device_alignment" (New-RoomArgs @{ deviceCodes = (Get-InlineCodes); codes = @("T_WALL"); toleranceMm = -1 })
        Assert-Ok $all "revit_check_device_alignment"
        $mine = @($all.Data.issues | Where-Object { $_.roomNumber -eq $RoomNumber } | Sort-Object { $_.elementId } -Descending)[0]
        Assert ($null -ne $mine) "Placed device not found by the alignment audit."
        foreach ($dev in @($mine.deviations)) { Assert ([math]::Abs($dev.deviationMm) -lt 1) "Placed device is $($dev.deviationMm) mm off on $($dev.axis)." }
        $script:Context.PlacedId = $mine.elementId
        "$how, element $($mine.elementId) on its mount"
    }

    Test-Case "W.2" "rotate + set elevation previews on the placed device" {
        $id = $script:Context.PlacedId
        if (-not $id) { return "SKIP: needs W.1" }
        $rot = Invoke-RevitTool "revit_preview_rotate_elements" @{ elementIds = @($id); rotateByDeg = 90 }
        Assert-Ok $rot "revit_preview_rotate_elements"
        Assert ((@($rot.Data.elements)[0].status) -eq "ready") "Rotate preview is not ready."
        $ele = Invoke-RevitTool "revit_preview_set_elevation" @{ elevations = @(@{ elementId = $id; elevationFromLevelMm = 1200; expectedElevationFromLevelMm = 300 }) }
        Assert-Ok $ele "revit_preview_set_elevation"
        Assert ((@($ele.Data.elements)[0].status) -eq "ready") "Elevation preview is not ready: $((@($ele.Data.elements)[0]).reason)"
        $stale = Invoke-RevitTool "revit_preview_set_elevation" @{ elevations = @(@{ elementId = $id; elevationFromLevelMm = 1200; expectedElevationFromLevelMm = 999 }) }
        Assert ((@($stale.Data.elements)[0].status) -eq "stale") "A wrong expected value should mark the element stale."
        "rotate ready, elevation ready, stale check works"
    }

    Test-Case "W.3" "set elevation apply moves the device to 1200 mm" {
        $id = $script:Context.PlacedId
        if (-not $id) { return "SKIP: needs W.1" }
        $r = Invoke-RevitTool "revit_set_elevation" @{ elementIds = @($id); elevationFromLevelMm = 1200 }
        $how = Assert-Approval $r "revit_set_elevation"
        $check = Invoke-RevitTool "revit_preview_set_elevation" @{ elementIds = @($id); elevationFromLevelMm = 1200 }
        Assert ((@($check.Data.elements)[0].status) -eq "unchanged") "Element is not at 1200 mm after the change."
        "$how"
    }

    Test-Case "W.4" "rotate apply turns the device 90 deg" {
        $id = $script:Context.PlacedId
        if (-not $id) { return "SKIP: needs W.1" }
        $before = Invoke-RevitTool "revit_preview_rotate_elements" @{ elementIds = @($id); rotateByDeg = 0.001 }
        $start = [double](@($before.Data.elements)[0].current -replace "[^0-9.\-]", "")
        $r = Invoke-RevitTool "revit_rotate_elements" @{ elementIds = @($id); rotateByDeg = 90 }
        $how = Assert-Approval $r "revit_rotate_elements"
        $after = Invoke-RevitTool "revit_preview_rotate_elements" @{ elementIds = @($id); angleDeg = (($start + 90) % 360) }
        Assert ((@($after.Data.elements)[0].status) -eq "unchanged") "Device does not face start + 90 deg after rotating."
        "$how, $start -> $(($start + 90) % 360) deg"
    }

    Test-Case "W.5" "assign room number" {
        if (-not $RoomParameter) { return "SKIP: pass -RoomParameter" }
        $id = $script:Context.PlacedId
        if (-not $id) { return "SKIP: needs W.1" }
        $p = Invoke-RevitTool "revit_preview_assign_room_to_elements" (New-RoomArgs @{ elementIds = @($id); roomParameter = $RoomParameter })
        Assert-Ok $p "revit_preview_assign_room_to_elements"
        $e = @($p.Data.elements)[0]
        Assert ($e.target -eq $RoomNumber) "Room found is '$($e.target)', expected '$RoomNumber'."
        if ($e.status -eq "unchanged") { return "already '$RoomNumber' (written on placement)" }
        $r = Invoke-RevitTool "revit_assign_room_to_elements" (New-RoomArgs @{ elementIds = @($id); roomParameter = $RoomParameter })
        $how = Assert-Approval $r "revit_assign_room_to_elements"
        $q = Invoke-RevitTool "revit_preview_assign_room_to_elements" (New-RoomArgs @{ elementIds = @($id); roomParameter = $RoomParameter })
        Assert ((@($q.Data.elements)[0].status) -eq "unchanged") "Room number was not written."
        "$how"
    }

    Test-Case "W.6" "create cable type, then it is skipped as existing" {
        if (-not $script:Context.CableNew) { return "SKIP: needs 53.1" }
        $r = Invoke-RevitTool "revit_create_cable_type" @{ sourceTypeName = $script:Context.CableSource; newName = $script:Context.CableNew }
        $how = Assert-Approval $r "revit_create_cable_type"
        $p = Invoke-RevitTool "revit_preview_create_cable_type" @{ sourceTypeName = $script:Context.CableSource; newName = $script:Context.CableNew }
        Assert ((@($p.Data.proposals)[0].action) -eq "skipExisting") "New cable type '$($script:Context.CableNew)' was not created."
        "$how, '$($script:Context.CableNew)'"
    }

    Write-Host "  -> Now Undo the test changes in Revit (or close without saving)." -ForegroundColor Magenta
}

# -- Summary ------------------------------------------------------------------

Write-Host ""
$pass = @($script:Results | Where-Object Result -eq "PASS").Count
$fail = @($script:Results | Where-Object Result -eq "FAIL").Count
$skip = @($script:Results | Where-Object Result -eq "SKIP").Count
Write-Host ("{0} passed, {1} failed, {2} skipped" -f $pass, $fail, $skip) -ForegroundColor $(if ($fail -gt 0) { "Red" } else { "Green" })
if ($script:Context.ImagePath) { Write-Host "Room image: $($script:Context.ImagePath)" }

$reportDir = Join-Path $env:TEMP "RevitMCP"
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
$report = Join-Path $reportDir ("room-devices-test-{0:yyyyMMdd-HHmmss}.json" -f (Get-Date))
$script:Results | ConvertTo-Json -Depth 4 | Set-Content -Path $report -Encoding UTF8
Write-Host "Report: $report"

if ($fail -gt 0) { exit 1 }
exit 0
