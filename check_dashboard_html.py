import paramiko

key = paramiko.RSAKey.from_private_key_file(r'd:\thongvamProject\RelayServer\VinaHostSSH', password='132456')
c = paramiko.SSHClient()
c.set_missing_host_key_policy(paramiko.AutoAddPolicy())
c.connect('103.126.161.38', username='root', pkey=key)

_, o, _ = c.exec_command('cat /opt/wg-dashboard/index.html')
print(o.read().decode())
c.close()
