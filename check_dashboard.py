import paramiko

key = paramiko.RSAKey.from_private_key_file(r'd:\thongvamProject\RelayServer\VinaHostSSH', password='132456')
c = paramiko.SSHClient()
c.set_missing_host_key_policy(paramiko.AutoAddPolicy())
c.connect('103.126.162.46', username='root', pkey=key)

def run(cmd):
    _, o, e = c.exec_command(cmd)
    out = o.read().decode()
    err = e.read().decode()
    print(f'\n=== {cmd[:80]} ===')
    print(out[:2000])
    if err:
        print('ERR:', err[:500])

run('systemctl status wg-dashboard --no-pager -l | tail -20')
run('curl -s -H "Authorization: Bearer wg-relay-2026" http://localhost:8080/api/admin/peers')
run('journalctl -u wg-dashboard -n 20 --no-pager')

c.close()
