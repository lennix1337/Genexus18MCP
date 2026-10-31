function J($x,$n=900){ $s=($x | ConvertTo-Json -Depth 8 -Compress); $s.Substring(0,[Math]::Min($n,$s.Length)) }
foreach ($n in 'WWTrn353','Trn353') {
  $g = CallTool 8 'genexus_wwp' @{ action='list'; name=$n } 90
  "wwp $n : " + (J $g 900)
}
Stop-LiveProbeProcess
