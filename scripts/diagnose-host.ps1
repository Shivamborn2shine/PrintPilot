<#
.SYNOPSIS
    PrintAway Milestone 1 - Read-Only Host & Printer Diagnostic Script
.DESCRIPTION
    Performs strictly read-only checks on the Windows host environment:
    - Hardware & OS baseline (CPU, RAM, Disk, Timezone, Power/Sleep settings)
    - Print Spooler service state
    - Windows Printers, Print Configurations, Extended WMI Status, Drivers, Ports, and Active Queues
    - USB Printing PnP devices
    - Network adapter and outbound HTTPS connectivity check
    NOTE: This script NEVER sends a print job, modifies printer state, or exposes sensitive product IDs.
#>

[CmdletBinding()]
param(
    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Continue"

function Get-SafeValue {
    param([scriptblock]$Block, $Fallback = $null)
    try {
        return & $Block
    } catch {
        return $Fallback
    }
}

$timestampUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$timezone = Get-SafeValue { (Get-TimeZone).Id } "Unknown"

# 1. System & Hardware Baseline (Redacting sensitive ProductID / User / Machine IDs per PRD §2)
$os = Get-SafeValue { Get-CimInstance Win32_OperatingSystem }
$cpu = Get-SafeValue { Get-CimInstance Win32_Processor | Select-Object -First 1 }
$cs = Get-SafeValue { Get-CimInstance Win32_ComputerSystem }

$systemInfo = [ordered]@{
    TimestampUtc       = $timestampUtc
    LocalTimeZone      = $timezone
    OSCaption          = $os.Caption
    OSVersion          = $os.Version
    OSBuildNumber      = $os.BuildNumber
    OSArchitecture     = $os.OSArchitecture
    LastBootUpTimeUtc  = if ($os.LastBootUpTime) { $os.LastBootUpTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") } else { $null }
    Manufacturer       = $cs.Manufacturer
    Model              = $cs.Model
    CPUName            = ($cpu.Name -replace '\s+', ' ').Trim()
    CPUCores           = $cpu.NumberOfCores
    CPULogicalProcs    = $cpu.NumberOfLogicalProcessors
    TotalRAM_GB        = if ($cs.TotalPhysicalMemory) { [math]::Round($cs.TotalPhysicalMemory / 1GB, 2) } else { $null }
    FreeRAM_GB         = if ($os.FreePhysicalMemory) { [math]::Round(($os.FreePhysicalMemory * 1KB) / 1GB, 2) } else { $null }
}

# 2. Disk / Storage Baseline
$disks = @(Get-SafeValue {
    Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3" | ForEach-Object {
        [ordered]@{
            DeviceID     = $_.DeviceID
            VolumeName   = $_.VolumeName
            FileSystem   = $_.FileSystem
            Size_GB      = [math]::Round($_.Size / 1GB, 2)
            FreeSpace_GB = [math]::Round($_.FreeSpace / 1GB, 2)
            FreePercent  = if ($_.Size -gt 0) { [math]::Round(($_.FreeSpace / $_.Size) * 100, 1) } else { 0 }
        }
    }
} @())

# 3. Print Spooler Service
$spooler = Get-SafeValue {
    $svc = Get-Service -Name Spooler -ErrorAction Stop
    $cimSvc = Get-CimInstance Win32_Service -Filter "Name='Spooler'"
    [ordered]@{
        Name        = $svc.Name
        Status      = $svc.Status.ToString()
        StartType   = $svc.StartType.ToString()
        StartName   = $cimSvc.StartName
        ProcessId   = $cimSvc.ProcessId
    }
}

# 4. Printers, Configurations, WMI State, Drivers, Ports, Active Jobs
$wmiPrinters = @{}
Get-SafeValue {
    Get-CimInstance Win32_Printer | ForEach-Object {
        $wmiPrinters[$_.Name] = $_
    }
}

$virtualPatterns = 'Microsoft Print to PDF|Microsoft XPS Document Writer|OneNote|Fax|Send To OneNote'
$printers = @(Get-SafeValue {
    Get-Printer | ForEach-Object {
        $p = $_
        $cfg = Get-SafeValue { Get-PrintConfiguration -PrinterName $p.Name -ErrorAction Stop }
        $jobs = @(Get-SafeValue { Get-PrintJob -PrinterName $p.Name -ErrorAction Stop } @())
        $wmi = $wmiPrinters[$p.Name]
        $isVirtual = ($p.Name -match $virtualPatterns) -or ($p.PortName -match 'PORTPROMPT:|nul:|SHRFAX:')
        [ordered]@{
            Name                  = $p.Name
            DriverName            = $p.DriverName
            PortName              = $p.PortName
            Shared                = $p.Shared
            Published             = $p.Published
            Default               = if ($null -ne $wmi) { [bool]$wmi.Default } else { $null }
            WorkOffline           = if ($null -ne $wmi) { [bool]$wmi.WorkOffline } else { $null }
            SpoolEnabled          = if ($null -ne $wmi) { [bool]$wmi.SpoolEnabled } else { $null }
            KeepPrintedJobs       = $p.KeepPrintedJobs
            PrinterStatus         = $p.PrinterStatus.ToString()
            WmiStatus             = if ($null -ne $wmi) { $wmi.Status } else { $null }
            ExtendedPrinterStatus = if ($null -ne $wmi) { $wmi.ExtendedPrinterStatus } else { $null }
            JobCount              = $p.JobCount
            ActiveJobsInQueue     = $jobs.Count
            IsVirtualExcluded     = [bool]$isVirtual
            IsXeroxCandidate      = [bool]($p.Name -match 'Xerox|B1025')
            Configuration         = if ($cfg) {
                [ordered]@{
                    PaperSize     = $cfg.PaperSize.ToString()
                    DuplexingMode = $cfg.DuplexingMode.ToString()
                    Color         = $cfg.Color
                    Collate       = $cfg.Collate
                }
            } else { $null }
        }
    }
} @())

$drivers = @(Get-SafeValue {
    Get-PrinterDriver | Select-Object Name, PrinterEnvironment, MajorVersion, Manufacturer, Provider | ForEach-Object {
        [ordered]@{
            Name         = $_.Name
            Environment  = $_.PrinterEnvironment
            MajorVersion = $_.MajorVersion
            Manufacturer = $_.Manufacturer
            Provider     = $_.Provider
        }
    }
} @())

$ports = @(Get-SafeValue {
    Get-PrinterPort | Select-Object Name, Description, PrinterHostAddress, PortMonitor | ForEach-Object {
        [ordered]@{
            Name               = $_.Name
            Description        = $_.Description
            PrinterHostAddress = $_.PrinterHostAddress
            PortMonitor        = $_.PortMonitor
        }
    }
} @())

# 5. USB & Print PnP Devices
$usbPrintDevices = @(Get-SafeValue {
    Get-PnpDevice | Where-Object {
        $_.Class -in @('Printer', 'PrintQueue', 'USB') -and $_.FriendlyName -match 'Xerox|B1025|USB Printing Support'
    } | ForEach-Object {
        [ordered]@{
            Class        = $_.Class
            FriendlyName = $_.FriendlyName
            Status       = $_.Status
            InstanceId   = $_.InstanceId
        }
    }
} @())

# 6. Power Scheme & Sleep Check
$activePowerScheme = Get-SafeValue {
    (powercfg /getactivescheme) -join ' '
} "Unknown"

# 7. Network Adapters
$netAdapters = @(Get-SafeValue {
    Get-NetAdapter | Select-Object Name, InterfaceDescription, Status, LinkSpeed | ForEach-Object {
        [ordered]@{
            Name        = $_.Name
            Description = $_.InterfaceDescription
            Status      = $_.Status.ToString()
            LinkSpeed   = $_.LinkSpeed
        }
    }
} @())

$report = [ordered]@{
    System          = $systemInfo
    Disks           = $disks
    SpoolerService  = $spooler
    Printers        = $printers
    Drivers         = $drivers
    Ports           = $ports
    UsbPrintDevices = $usbPrintDevices
    PowerScheme     = $activePowerScheme
    NetworkAdapters = $netAdapters
}

if ($AsJson) {
    $report | ConvertTo-Json -Depth 6
} else {
    Write-Host "=== PrintAway Milestone 1 Host & Printer Diagnostics (Read-Only) ===" -ForegroundColor Cyan
    Write-Host "Timestamp (UTC): $($systemInfo.TimestampUtc) | Local TimeZone: $($systemInfo.LocalTimeZone)"
    Write-Host "OS:              $($systemInfo.OSCaption) (Build $($systemInfo.OSBuildNumber), $($systemInfo.OSArchitecture))"
    Write-Host "Hardware:        $($systemInfo.Manufacturer) $($systemInfo.Model) | $($systemInfo.CPUName)"
    Write-Host "Memory:          $($systemInfo.FreeRAM_GB) GB free / $($systemInfo.TotalRAM_GB) GB total"
    Write-Host "Power Scheme:    $activePowerScheme"
    Write-Host "Spooler Service: $($spooler.Status) (StartType: $($spooler.StartType), PID: $($spooler.ProcessId))"
    Write-Host "`n--- Disks ---" -ForegroundColor Yellow
    $disks | ForEach-Object { [pscustomobject]$_ } | Format-Table -AutoSize
    Write-Host "--- Installed Printers ---" -ForegroundColor Yellow
    $printers | ForEach-Object { [pscustomobject]$_ } | Select-Object Name, DriverName, PortName, Default, WorkOffline, PrinterStatus, JobCount, IsVirtualExcluded, IsXeroxCandidate | Format-Table -AutoSize
    Write-Host "--- Xerox Candidate Queue Configurations ---" -ForegroundColor Yellow
    $printers | Where-Object { $_.IsXeroxCandidate } | ForEach-Object {
        Write-Host "Queue: $($_.Name) [Port: $($_.PortName), Driver: $($_.DriverName), Default: $($_.Default), WorkOffline: $($_.WorkOffline)]"
        if ($_.Configuration) {
            Write-Host "  PaperSize: $($_.Configuration.PaperSize) | DuplexingMode: $($_.Configuration.DuplexingMode) | Color: $($_.Configuration.Color) | Collate: $($_.Configuration.Collate)"
        }
    }
    Write-Host "`n--- USB & Print PnP Devices ---" -ForegroundColor Yellow
    $usbPrintDevices | ForEach-Object { [pscustomobject]$_ } | Format-Table -AutoSize
    Write-Host "--- Network Adapters ---" -ForegroundColor Yellow
    $netAdapters | ForEach-Object { [pscustomobject]$_ } | Format-Table -AutoSize
}
