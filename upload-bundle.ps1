# 把"零部署安装包"作为附件上传到 v1.3.3 release
# 用 api.github.com（github.com 的直连通道时常不通，API 一直可用）
$ErrorActionPreference = 'Stop'
$dist = 'I:\dsh-bundle\dist'
$owner = 'XianyuKira'; $repo = 'dsh-desktop'; $tag = 'v1.3.3'

# 凭据：写查询到临时文件，让 cmd 做 stdin 重定向（.NET 直写 stdin 在本机不可靠）
$qf = Join-Path $env:TEMP ('gcm-' + [guid]::NewGuid().ToString('N').Substring(0,8) + '.txt')
[System.IO.File]::WriteAllText($qf, "protocol=https`nhost=github.com`n`n", (New-Object System.Text.UTF8Encoding($false)))
$token = $null
try {
    $out = cmd.exe /c "git credential fill < `"$qf`"" 2>&1 | Out-String
    foreach ($line in ($out -split "`n")) {
        $t = $line.Trim()
        if ($t.StartsWith('password=')) { $token = $t.Substring('password='.Length).Trim() }
    }
} finally { Remove-Item $qf -Force -ErrorAction SilentlyContinue }
if (-not $token) { throw '没有取到 GitHub 凭据' }
$headers = @{
    Authorization          = "Bearer $token"
    Accept                 = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent'           = 'dsh-desktop-release'
}
function Redact([string]$s) { if ($s) { $s.Replace($token, '***') } else { $s } }

Write-Host '=== 1. 找到 release ==='
$rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$repo/releases/tags/$tag" -Headers $headers -TimeoutSec 60
Write-Host "  $($rel.html_url)"
Write-Host '  现有附件:'
$rel.assets | ForEach-Object { Write-Host ("    {0}  {1:N2} MB" -f $_.name, ($_.size / 1MB)) }

Write-Host ''
Write-Host '=== 2. 上传零部署包 ==='
# 两个包重命名，让下载者一眼看懂
$map = @(
    @{ File = 'payload-1.3.3.zip';      Name = 'zero-deploy-payload-1.3.3.zip' },
    @{ File = 'install.ps1';            Name = 'zero-deploy-install.ps1' },
    @{ File = '安装.cmd';                Name = 'zero-deploy-安装.cmd' },
    @{ File = '安装说明.txt';             Name = 'zero-deploy-说明.txt' }
)
foreach ($item in $map) {
    $path = Join-Path $dist $item.File
    if (-not (Test-Path $path)) { Write-Host "  缺少 $($item.File)"; continue }
    $existing = $rel.assets | Where-Object { $_.name -eq $item.Name }
    if ($existing) { Write-Host "  已存在，跳过: $($item.Name)"; continue }

    $url = "$($rel.upload_url -replace '\{\?name,label\}', '')?name=$([uri]::EscapeDataString($item.Name))"
    $ctype = if ($item.Name -like '*.zip') { 'application/zip' } else { 'application/octet-stream' }
    $mb = [math]::Round((Get-Item $path).Length / 1MB, 2)
    try {
        $r = Invoke-RestMethod -Uri $url -Method Post -Headers $headers -ContentType $ctype -InFile $path -TimeoutSec 3600
        Write-Host ("  OK   {0}  ({1} MB)" -f $item.Name, $mb)
    } catch {
        Write-Host "  FAIL $($item.Name): $(Redact $_.Exception.Message)"
    }
}

Write-Host ''
Write-Host '=== 3. 最终附件列表 ==='
$final = Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$repo/releases/tags/$tag" -Headers $headers -TimeoutSec 60
$final.assets | Sort-Object size -Descending | ForEach-Object {
    Write-Host ("    {0,-46} {1,8:N2} MB" -f $_.name, ($_.size / 1MB))
}
Write-Host ''
Write-Host "  release: $($final.html_url)"
