p = r'd:\thongvamProject\RelayServer\dashboard\index.html'
with open(p, encoding='utf-8') as f:
    c = f.read()

# Fix delete buttons - add trash emoji back
c = c.replace(
    "onclick=\"deletePeer('${p.id}')\">" + " Remove</button>",
    "onclick=\"deletePeer('${p.id}')\">🗑 Remove</button>"
)
c = c.replace(
    "onclick=\"deletePeer('${p.id}')\">" + " Delete</button>",
    "onclick=\"deletePeer('${p.id}')\">🗑 Delete</button>"
)
# Fix approve button - add checkmark back
c = c.replace(
    "onclick=\"approvePeer('${p.id}')\">",
    "onclick=\"approvePeer('${p.id}')\">✓"
)

with open(p, 'w', encoding='utf-8') as f:
    f.write(c)

# Verify
import subprocess
result = subprocess.run(['python', '-c', f'''
with open(r"{p}", encoding="utf-8") as f:
    content = f.read()
import re
buttons = re.findall(r'onclick="(?:delete|approve)Peer[^"]+">([^<]+)<', content)
for b in buttons:
    print(repr(b))
'''], capture_output=True, text=True)
print(result.stdout)
print("Done")
