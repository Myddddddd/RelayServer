with open(r'd:\thongvamProject\RelayServer\dashboard\index.html', encoding='utf-8') as f:
    c = f.read()
import re
buttons = re.findall(r'onclick="(?:delete|approve|reject)Peer[^"]+">([^<]+)<', c)
for b in set(buttons):
    print(repr(b))
