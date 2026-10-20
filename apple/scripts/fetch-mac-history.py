"""Restore only update archives advertised by our production feed; 404 means first release.

fetch-mac-history.py SERVER DESTINATION [FORMER_SERVER ...]

After Spindle Cloud moves to another address, the published feed still names the former one. Archives there are
accepted too, and the restored feed names the new address, so the next feed is all on it.
"""
import pathlib
import shutil
import sys
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET

server, destination, *former = sys.argv[1:]
prefix = server.rstrip('/') + '/downloads/spindle-mac/'
prefixes = [prefix] + [address.rstrip('/') + '/downloads/spindle-mac/' for address in former]
folder = pathlib.Path(destination)
folder.mkdir(parents=True, exist_ok=True)
try:
    with urllib.request.urlopen(prefix + 'appcast.xml', timeout=60) as response:
        feed = response.read()
except urllib.error.HTTPError as error:
    if error.code != 404:
        raise
    print('No published feed; preparing the first release.')
    sys.exit(0)

root = ET.fromstring(feed)
urls = {element.get('url') for element in root.iter('enclosure')}
for url in sorted(urls):
    owner = next((candidate for candidate in prefixes if url and url.startswith(candidate)), None)
    if owner is None:
        raise ValueError('Feed references an archive outside the Mac downloads directory')
    name = url[len(owner):]
    if urllib.parse.unquote(name) != name or '/' in name or pathlib.Path(name).suffix not in {'.dmg', '.delta'}:
        raise ValueError('Unexpected update archive name')
    with urllib.request.urlopen(url, timeout=120) as response, (folder / name).open('wb') as output:
        shutil.copyfileobj(response, output)
# The archives' signatures don't cover their address, so the restored feed can name the new one.
text = feed.decode()
for former_prefix in prefixes[1:]:
    text = text.replace(former_prefix, prefix)
(folder / 'appcast.xml').write_text(text)
print(f'Restored {len(urls)} update archives.')
