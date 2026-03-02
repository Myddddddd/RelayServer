import paramiko
import time

key = paramiko.RSAKey.from_private_key_file(r'd:\thongvamProject\RelayServer\VinaHostSSH', password='132456')
c = paramiko.SSHClient()
c.set_missing_host_key_policy(paramiko.AutoAddPolicy())
c.connect('103.126.161.38', username='root', pkey=key)

sftp = c.open_sftp()
print("Uploading main.py...")
sftp.put(r'd:\thongvamProject\RelayServer\dashboard\main.py', '/opt/wg-dashboard/main.py')
print("Uploading index.html...")
sftp.put(r'd:\thongvamProject\RelayServer\dashboard\index.html', '/opt/wg-dashboard/index.html')
sftp.close()

print("Restarting wg-dashboard service...")
_, o, e = c.exec_command('systemctl restart wg-dashboard && sleep 2 && systemctl is-active wg-dashboard')
out = o.read().decode().strip()
err = e.read().decode().strip()
print("Service status:", out)
if err:
    print("ERR:", err)

# Check for startup errors
_, o, _ = c.exec_command('journalctl -u wg-dashboard -n 15 --no-pager')
print("\nRecent logs:")
print(o.read().decode())

c.close()
print("Deploy complete.")
