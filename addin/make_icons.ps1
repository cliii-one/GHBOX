Add-Type -AssemblyName System.Drawing
$dir = 'c:\Users\Administrator\Desktop\ArcGIS\GHBOX\addin\Images'
New-Item -ItemType Directory -Force $dir | Out-Null

# Pro 图标配色：主色蓝（折线/主体）、强调橙（命中的弧/角）、警示红（顶点）
$blue   = [System.Drawing.Color]::FromArgb(255, 54, 111, 168)   # #366FA8
$gray   = [System.Drawing.Color]::FromArgb(255, 130, 130, 130)  # 普通直线段
$orange = [System.Drawing.Color]::FromArgb(255, 232, 145, 47)   # #E8912F 命中部分
$red    = [System.Drawing.Color]::FromArgb(255, 224, 75, 75)    # #E04B4B 顶点

function New-SearchArcIcon([string]$path, [int]$sz) {
    $bmp = New-Object System.Drawing.Bitmap($sz, $sz)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $w = [Math]::Max(2.0, $sz / 11.0)   # 线宽随尺寸缩放
    $penGray = New-Object System.Drawing.Pen($gray, $w)
    $penArc  = New-Object System.Drawing.Pen($orange, ($w * 1.35))
    foreach ($p in @($penGray, $penArc)) {
        $p.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $p.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    }

    # 布局（比例坐标）：灰色直线段 → 橙色圆弧（命中） → 灰色直线段
    $ly = 0.62 * $sz   # 直线段 y
    $x1 = 0.06 * $sz; $xa = 0.33 * $sz; $xb = 0.67 * $sz; $x2 = 0.94 * $sz
    $g.DrawLine($penGray, [float]$x1, [float]$ly, [float]$xa, [float]$ly)
    $g.DrawLine($penGray, [float]$xb, [float]$ly, [float]$x2, [float]$ly)

    # 上拱圆弧：矩形从 (xa, ly-h) 到 (xb, ly)，画上半周
    $h = 0.42 * $sz
    $rect = New-Object System.Drawing.RectangleF([float]($xa), [float]($ly - $h), [float]($xb - $xa), [float]($h * 2))
    $g.DrawArc($penArc, $rect, 180, 180)

    # 弧段两端小圆点（节点标记）
    $d = [Math]::Max(3.0, $sz / 8.0)
    $b = New-Object System.Drawing.SolidBrush($blue)
    $g.FillEllipse($b, [float]($xa - $d / 2), [float]($ly - $d / 2), $d, $d)
    $g.FillEllipse($b, [float]($xb - $d / 2), [float]($ly - $d / 2), $d, $d)

    $b.Dispose(); $penGray.Dispose(); $penArc.Dispose(); $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}

function New-FindAngleIcon([string]$path, [int]$sz) {
    $bmp = New-Object System.Drawing.Bitmap($sz, $sz)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $w = [Math]::Max(2.0, $sz / 11.0)
    $penBlue = New-Object System.Drawing.Pen($blue, $w)
    $penBlue.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $penBlue.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round

    # V 形锐角：左上 → 下顶点 → 右上
    $top = 0.16 * $sz
    $vx = 0.5 * $sz; $vy = 0.80 * $sz
    $lx = 0.10 * $sz; $rx = 0.90 * $sz
    $pts = @(
        (New-Object System.Drawing.PointF([float]$lx, [float]$top)),
        (New-Object System.Drawing.PointF([float]$vx, [float]$vy)),
        (New-Object System.Drawing.PointF([float]$rx, [float]$top))
    )
    $g.DrawLines($penBlue, $pts)

    # 顶点处角度弧（橙色，标示夹角范围）
    $r = 0.26 * $sz
    $penAng = New-Object System.Drawing.Pen($orange, [Math]::Max(1.5, $w * 0.7))
    $rect = New-Object System.Drawing.RectangleF([float]($vx - $r), [float]($vy - $r), [float]($r * 2), [float]($r * 2))
    $ang1 = [Math]::Atan2($top - $vy, $lx - $vx) * 180.0 / [Math]::PI   # 左臂方向（GDI 角度，顺时针）
    $ang2 = [Math]::Atan2($top - $vy, $rx - $vx) * 180.0 / [Math]::PI   # 右臂方向
    if ($ang2 -lt $ang1) { $t = $ang1; $ang1 = $ang2; $ang2 = $t }
    $g.DrawArc($penAng, $rect, [float]$ang1, [float]($ang2 - $ang1))

    # 顶点红点（命中的尖锐角顶点）
    $d = [Math]::Max(4.0, $sz / 6.0)
    $b = New-Object System.Drawing.SolidBrush($red)
    $g.FillEllipse($b, [float]($vx - $d / 2), [float]($vy - $d / 2), $d, $d)

    $b.Dispose(); $penAng.Dispose(); $penBlue.Dispose(); $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}

New-SearchArcIcon "$dir\SearchArc16.png" 16
New-SearchArcIcon "$dir\SearchArc32.png" 32
New-FindAngleIcon "$dir\FindAngle16.png" 16
New-FindAngleIcon "$dir\FindAngle32.png" 32

# 工具箱级品牌图标 GHBox16/32.png：不自绘，从 ArcGIS Pro 官方资源库提取的
#   geoprocessingtoolbox16/32.png（Pro 目录树中 GP 工具箱的经典图标，Esri 原版）。
#   提取方式：ResourceReader 打开 D:\ArcGIS\Pro\bin\ArcGIS.Desktop.Resources.dll 的
#   g.resources 流，按条目名取出 PNG 字节写文件（一次性操作，已入库）。
#   勿在本脚本中重绘这两个文件；如需更换，重新提取或替换 PNG 即可。
Get-ChildItem $dir | ForEach-Object { "$($_.Name) $($_.Length)" }
