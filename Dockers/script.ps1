# === Script chỉ thêm vào file .env (không xóa nội dung cũ) ===

# Sinh PG_PASS an toàn (khoảng 72 ký tự)
$pgpass = [Convert]::ToBase64String((1..54 | ForEach-Object { Get-Random -Minimum 0 -Maximum 256 }))
"PG_PASS=$pgpass" | Out-File -FilePath .env -Encoding utf8 -Append

# Sinh AUTHENTIK_SECRET_KEY
$secret = [Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Minimum 0 -Maximum 256 }))
"AUTHENTIK_SECRET_KEY=$secret" | Out-File -FilePath .env -Encoding utf8 -Append

# Thêm các biến mặc định (nếu chưa có)
"PG_USER=authentik" | Out-File -FilePath .env -Encoding utf8 -Append
"PG_DB=authentik" | Out-File -FilePath .env -Encoding utf8 -Append

Write-Host "Đã thêm PG_PASS và AUTHENTIK_SECRET_KEY vào file .env thành công!" -ForegroundColor Green
Write-Host "`nNội dung hiện tại của file .env:" -ForegroundColor Cyan
Get-Content .env