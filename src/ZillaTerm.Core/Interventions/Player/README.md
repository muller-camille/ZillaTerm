# Décodeur Brotli du lecteur des écrans

`brotli-decode.js` est le décodeur Brotli de référence en JavaScript, tel que publié par Google, sans
modification :

- source : <https://github.com/google/brotli/blob/86ee1f65f62d6272345dee751d49e7d1f01c2b11/js/decode.js>
- SHA-256 : `7fd3ecb658c9c7e389eaf0aaf7e0adcc8ba47e043c21bdefb8c3caa83d893d35`
- licence : MIT, `LICENSE.brotli.txt` (copie du fichier `LICENSE` du dépôt, aussi livrée dans le dossier `licenses` de
  chaque version)

ZillaTerm l'embarque dans le script du lecteur des écrans enregistrés du rapport d'intervention
(`InterventionScreenPlayer`) : les navigateurs ne décompressent nativement que le deflate, et les images d'écran sont
compressées en Brotli depuis la 0.42. Seule sa dernière ligne (`export let BrotliDecode`) est réécrite à la volée en
déclaration locale : le rapport n'autorise qu'un script, sans module. Le décodeur n'utilise ni `eval` ni
`new Function`, et ne fait aucun accès réseau.
