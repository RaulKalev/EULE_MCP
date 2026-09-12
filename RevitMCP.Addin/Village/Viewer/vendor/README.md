# Vendored viewer dependencies

`village-three.min.js` is a tree-shaken, minified IIFE bundle of the parts of
[three.js](https://threejs.org/) r0.186.0 that the village viewer uses, plus `GLTFLoader`.
It exposes a single global, `VillageGL`.

It is vendored rather than fetched so the viewer keeps its "loads nothing from the network"
property: the connector serves it from `/vendor/village-three.min.js` on the loopback listener.

Rebuild it with:

    npm pack three@0.186.0 && tar -xzf three-0.186.0.tgz
    npx esbuild entry.js --bundle --minify --format=iife --global-name=VillageGL \
        --outfile=village-three.min.js --legal-comments=none

where `entry.js` re-exports only the symbols the viewer needs (see git history).
three.js is MIT licensed; the licence travels with the bundle's source package.
