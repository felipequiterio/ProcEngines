import pathlib
root = pathlib.Path(__file__).parent
page = (root/'src/page.html').read_text(encoding='utf-8')
page = page.replace('/*PHYSICS*/', (root/'src/physics.js').read_text(encoding='utf-8'))
page = page.replace('/*APP*/', (root/'src/app.js').read_text(encoding='utf-8'))
(root/'procengines.html').write_text(page, encoding='utf-8')
# local preview wrapper with a doctype (the publish skeleton adds one)
(root/'preview.html').write_text('<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover"></head><body>'+page+'</body></html>', encoding='utf-8')
print(len(page))
