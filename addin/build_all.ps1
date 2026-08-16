# ============================================================
# 一键构建所有 ArcGIS Pro 版本的 GHBoxAddIn 安装包
# 原理：
#   1) 按 csproj 的 BuildFlavor 参数分别编译三档 Pro 版本
#   2) 打包时把 Config.daml 的 desktopVersion 替换为对应最低版本
#   3) 用 .NET ZipFile API（与 Esri 官方 targets 同款）打 .esriAddInX 包
#   4) 可选：本机注册（-Install 开关）
# 用法：
#   powershell -File build_all.ps1            # 只构建，产出 dist/ 下三个包
#   powershell -File build_all.ps1 -Install   # 构建 + 注册到本机 Pro 3.6
# ============================================================
param([switch]$Install)

$ErrorActionPreference = 'Stop'

# ---- 配置区：Pro 版本 → (BuildFlavor, DAML最低版本, 说明) ----
$targets = @(
    @{ Flavor = 'Pro30'; DamlVersion = '3.0'; Label = 'Pro 3.0~3.2（.NET 6）' }
    @{ Flavor = 'Pro33'; DamlVersion = '3.3'; Label = 'Pro 3.3~3.6（.NET 8）' }
    @{ Flavor = 'Pro37'; DamlVersion = '3.7'; Label = 'Pro 3.7+（.NET 10）' }
)

$addinDir  = $PSScriptRoot                 # addin 目录
$csproj    = Join-Path $addinDir 'GHBoxAddIn.csproj'
$distDir   = Join-Path $addinDir 'dist'
New-Item $distDir -ItemType Directory -Force | Out-Null

# 本机 ArcGIS Pro（仅 -Install 时需要；注册工具 RegisterAddIn.exe 随 Pro 安装）
$registerExe = 'D:\ArcGIS\Pro\bin\RegisterAddIn.exe'

Add-Type -AssemblyName System.IO.Compression.FileSystem

foreach ($t in $targets) {
    $flavor = $t.Flavor
    Write-Host "`n========== 构建 $($t.Label) ==========" -ForegroundColor Cyan

    # 1. 编译（NuGet 还原 + 构建）
    #    CopyLocalLockFileAssemblies=true：第三方依赖（ClosedXML 等）复制到 bin，
    #    供下方"依赖 DLL 进包"步骤打进 Install/ 目录（Esri 包已用 ExcludeAssets=runtime 排除，不会重复）。
    dotnet build $csproj -c Release -p:BuildFlavor=$flavor -p:CopyLocalLockFileAssemblies=true
    if ($LASTEXITCODE -ne 0) { Write-Host "[$flavor] 编译失败，跳过该版本。" -ForegroundColor Red; continue }

    $binDir = Join-Path $addinDir "bin\Release"
    if (-not (Test-Path (Join-Path $binDir 'GHBoxAddIn.dll'))) {
        Write-Host "[$flavor] 未找到 DLL 输出，跳过。" -ForegroundColor Red; continue
    }

    # 2. 打包目录：Config.daml（替换 desktopVersion）+ Install/DLL
    $staging = Join-Path $addinDir "obj\pkg_$flavor"
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    New-Item $staging -ItemType Directory -Force | Out-Null
    New-Item (Join-Path $staging 'Install') -ItemType Directory -Force | Out-Null

    # desktopVersion 是"最低兼容版本"：写成各档最低值，供 Pro 判断能否加载
    $daml = Get-Content (Join-Path $addinDir 'Config.daml') -Raw -Encoding UTF8
    $daml = $daml -replace 'desktopVersion="[^"]*"', "desktopVersion=`"$($t.DamlVersion)`""
    [IO.File]::WriteAllText((Join-Path $staging 'Config.daml'), $daml, [Text.UTF8Encoding]::new($false))

    # 工具箱级图标：AddInInfo 的 <Image> 用包内相对路径（Images\GHBox32.png），
    # 必须把图标目录复制进包根（与 Config.daml 同级），否则 Pro 加载项列表显示无图标
    Copy-Item (Join-Path $addinDir 'Images') (Join-Path $staging 'Images') -Recurse -Force

    Copy-Item (Join-Path $binDir 'GHBoxAddIn.dll')       (Join-Path $staging 'Install') -Force
    Copy-Item (Join-Path $binDir 'GHBoxAddIn.deps.json') (Join-Path $staging 'Install') -Force

    # 第三方依赖 DLL（ClosedXML 及其依赖）也必须进包：Pro 的 AssemblyCache 只认 Install/ 下的文件
    Get-ChildItem $binDir -Filter '*.dll' |
        Where-Object { $_.Name -ne 'GHBoxAddIn.dll' } |
        ForEach-Object { Copy-Item $_.FullName (Join-Path $staging 'Install') -Force }

    # 3. 打 .esriAddInX 包（.NET ZipFile API，Esri 官方 targets 同款）
    $pkg = Join-Path $distDir "GHBoxAddIn_$flavor.esriAddInX"
    if (Test-Path $pkg) { Remove-Item $pkg -Force }
    [IO.Compression.ZipFile]::CreateFromDirectory($staging, $pkg, [IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Host "[$flavor] 产出：$pkg"

    # 4. 可选注册（仅本机 Pro 3.6 → Pro33 包）
    if ($Install -and $flavor -eq 'Pro33') {
        if (Test-Path $registerExe) {
            $p = Start-Process -FilePath $registerExe -ArgumentList '/s', "`"$pkg`"" -Wait -PassThru
            Write-Host "[$flavor] RegisterExitCode=$($p.ExitCode)（重启 Pro 生效）"
        } else {
            Write-Host "[$flavor] 未找到 RegisterAddIn.exe，跳过注册。" -ForegroundColor Yellow
        }
    }
}

Write-Host "`n全部完成。安装包位于：$distDir" -ForegroundColor Green
Write-Host '分发给使用者：双击对应版本的 .esriAddInX 即可安装（需匹配其 ArcGIS Pro 版本）。'
