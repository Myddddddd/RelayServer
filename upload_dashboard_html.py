import paramiko

key = paramiko.RSAKey.from_private_key_file(r'd:\thongvamProject\RelayServer\VinaHostSSH', password='132456')
c = paramiko.SSHClient()
c.set_missing_host_key_policy(paramiko.AutoAddPolicy())
c.connect('103.126.162.46', username='root', pkey=key)

sftp = c.open_sftp()
sftp.put(r'd:\thongvamProject\RelayServer\dashboard\index.html', '/opt/wg-dashboard/index.html')
sftp.close()

# No need to restart — main.py serves the file directly from disk
print("Uploaded index.html")

# Verify
_, o, _ = c.exec_command('ls -la /opt/wg-dashboard/index.html')
print(o.read().decode())
c.close()
