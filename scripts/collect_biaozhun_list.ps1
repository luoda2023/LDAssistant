#Requires -Version 5.1
<#
.SYNOPSIS
biaozhun.org 国家标准列表采集脚本
#>

param(
    [int]$StartPage = 1,
    [int]$EndPage = 50,
    [string]$Category = "guojia",
    [string]$OutputFile = "C:\ZCODE\data\biaozhun_${Category}_list.csv",
    [string]$LogFile = "C:\ZCODE\logs\biaozhun_${Category}_list.log"
)

Add-Type -AssemblyName System.Web

# Write UTF-8 BOM header
$headerLine = '"标准名称","标准链接","标准编号","ICS分类","CCS分类","英文标题","标准分类","标准状态","发布日期","实施日期"'
[System.IO.File]::WriteAllText($OutputFile, $headerLine + "`r`n", [System.Text.Encoding]::UTF8)

$baseUrl = "https://www.biaozhun.org/${Category}/list-1-{0}.html"
$successCount = 0
$failCount = 0
$startTime = Get-Date

function Write-Log {
    param([string]$Message)
    $time = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $logLine = "$time | $Message"
    Add-Content -Path $LogFile -Value $logLine
    Write-Host $logLine
}

Write-Log "===== 开始采集 biaozhun.org ${Category} ====="
Write-Log "页面范围: $StartPage ~ $EndPage"
Write-Log "输出文件: $OutputFile"

for ($page = $StartPage; $page -le $EndPage; $page++) {
    $url = $baseUrl -f $page
    Write-Log "正在采集第 $page 页: $url"
    
    try {
        $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 30 -ErrorAction Stop
        $html = $response.Content
        
        # Split by <li> tags
        $items = [regex]::Split($html, '(?<=</li>)\s*(?=<li>)')
        
        $pageCount = 0
        foreach ($itemBlock in $items) {
            if ($itemBlock -notmatch '<a href="/guojia/\d+\.html"') { continue }
            
            # 提取标准链接和标题
            $titleMatch = [regex]::Match($itemBlock, 'href="(/guojia/\d+\.html)"[^>]*>(.+?)</a>')
            if (!$titleMatch.Success) { continue }
            
            $link = "https://www.biaozhun.org" + $titleMatch.Groups[1].Value
            $fullTitle = $titleMatch.Groups[2].Value.Trim()
            
            # 解析标准编号和名称
            $idx = $fullTitle.IndexOf(' ')
            if ($idx -gt 0) {
                $stdCode = $fullTitle.Substring(0, $idx)
                $stdName = $fullTitle.Substring($idx+1)
            } else {
                $stdCode = ""
                $stdName = $fullTitle
            }
            
            # ICS分类
            $icsMatch = [regex]::Match($itemBlock, 'ICS分类号[^:]*:\s*<em>([^<]+)</em>')
            if ($icsMatch.Success) { $ics = $icsMatch.Groups[1].Value.Trim() } else { $ics = "" }
            
            # CCS分类
            $ccsMatch = [regex]::Match($itemBlock, 'CCS[^:]*:\s*<em>([^<]+)</em>')
            if ($ccsMatch.Success) { $ccs = $ccsMatch.Groups[1].Value.Trim() } else { $ccs = "" }
            
            # 英文标题
            $enMatch = [regex]::Match($itemBlock, '英文标题\s*<span[^>]*>([^<]+)</span>')
            if ($enMatch.Success) { $enTitle = $enMatch.Groups[1].Value.Trim() } else { $enTitle = "" }
            
            # 标准分类 (取第一个)
            $catMatch = [regex]::Match($itemBlock, '标准分类\s*<a[^>]*><b>([^<]+)</b></a>')
            if ($catMatch.Success) { $category = $catMatch.Groups[1].Value.Trim() } else { $category = "" }
            
            # 标准状态
            $stateMatch = [regex]::Match($itemBlock, 'class="state">([^<]+)<')
            if ($stateMatch.Success) { $state = $stateMatch.Groups[1].Value.Trim() } else { $state = "" }
            
            # 发布日期
            $pubMatch = [regex]::Match($itemBlock, '发布日期[^:]*:<em>([^<]+)</em>')
            if ($pubMatch.Success) { $pubDate = $pubMatch.Groups[1].Value.Trim() } else { $pubDate = "" }
            
            # 实施日期
            $impMatch = [regex]::Match($itemBlock, '实施日期[^:]*:<em>([^<]+)</em>')
            if ($impMatch.Success) { $impDate = $impMatch.Groups[1].Value.Trim() } else { $impDate = "" }
            
            # Build CSV line - escape quotes
            $escName = $stdName -replace '"', '""'
            $escCode = $stdCode -replace '"', '""'
            $escLink = $link -replace '"', '""'
            $escIcs = $ics -replace '"', '""'
            $escCcs = $ccs -replace '"', '""'
            $escEn = $enTitle -replace '"', '""'
            $escCat = $category -replace '"', '""'
            $escState = $state -replace '"', '""'
            $escPub = $pubDate -replace '"', '""'
            $escImp = $impDate -replace '"', '""'
            
            $line = "`"${escName}`",`"${escLink}`",`"${escCode}`",`"${escIcs}`",`"${escCcs}`",`"${escEn}`",`"${escCat}`",`"${escState}`",`"${escPub}`",`"${escImp}`""
            [System.IO.File]::AppendAllText($OutputFile, $line + "`r`n", [System.Text.Encoding]::UTF8)
            $pageCount++
        }
        
        $successCount += $pageCount
        Write-Log "第 $page 页完成，提取 $pageCount 条，累计 $successCount 条"
        
        # 礼貌延迟
        Start-Sleep -Milliseconds 500
    }
    catch {
        $failCount++
        Write-Log "第 $page 页失败: $_"
        Start-Sleep -Seconds 5
    }
}

$endTime = Get-Date
$duration = ($endTime - $startTime).TotalMinutes
Write-Log "===== 采集完成 ====="
Write-Log "成功: $successCount 条, 失败: $failCount 页"
Write-Log "耗时: $([math]::Round($duration, 2)) 分钟"
Write-Host "`n===== 采集完成！共计 $successCount 条标准 ====="
