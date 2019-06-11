$year = (Get-Date).Year;
$quarter = [Math]::ceiling((Get-date -f MM)/3);
$month = (Get-Date).Month;
$monthNumber = $month - 3 * ($quarter - 1);
$day = (Get-Date).Day;
(Get-Content Program.cs) | ForEach-Object { $_ -replace "const string BuildDate = .*", "const string BuildDate = `"$($year).$($quarter).$($monthNumber).$($day)`";" } | Set-Content Program.cs