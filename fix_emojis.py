p = r'd:\thongvamProject\RelayServer\dashboard\index.html'
with open(p, encoding='utf-8') as f:
    c = f.read()

fixes = [
    # Reject button
    ("onclick=\"rejectPeer('${p.id}')\">" + " Reject</button>",
     "onclick=\"rejectPeer('${p.id}')\">✗ Reject</button>"),
    # Approve toast
    ("showToast(' Approved! VPN IP: '+d.vpn_ip)",
     "showToast('✅ Approved! VPN IP: '+d.vpn_ip)"),
    # Online status dot
    ("'status-online\"> Online</span>'",
     "'status-online\">● Online</span>'"),
    # Also fix ● in the string
    ('status-online"> Online</span>',
     'status-online">● Online</span>'),
]

for old, new in fixes:
    if old in c:
        c = c.replace(old, new)
        print(f"Fixed: {repr(old[:50])} -> {repr(new[:50])}")
    else:
        print(f"NOT FOUND: {repr(old[:60])}")

with open(p, 'w', encoding='utf-8') as f:
    f.write(c)
print("Done")
