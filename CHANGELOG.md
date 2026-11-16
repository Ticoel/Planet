# Historique des versions

## 1.0.0 — 2026-11-16

Première version complète : une petite planète « éprouvette » (2 000 km de rayon) entièrement
procédurale et autonome, de sa formation à sa vie géologique et climatique, à explorer de
l'orbite au sol.

- **Planète** : tectonique des plaques sur 4,5 milliards d'années, relief, volcans, cratères,
  glaciations ; étoile qui vieillit, Lune et marées.
- **Climat** : saisons, températures, vents, cycle de l'eau, océans (courants, remontées d'eau
  froide, banquise), climats de Köppen, chimie (carbone, azote, phosphore, soufre, fer).
- **Eau** : fleuves selon leur débit, torrents, lacs, deltas et vasières, eau souterraine,
  sources et résurgences, glace des lacs et des fleuves, flaques et sols craquelés.
- **Planète vivante** : éruptions (dont fissurales), séismes, glissements de terrain, lahars,
  avalanches, crues, tsunamis, cyclones, tornades, tempêtes de sable, icebergs, aurores, étoiles
  filantes, brouillards, arcs-en-ciel ; milieux hydrothermaux.
- **Exploration** : terrain détaillé jusqu'au mètre, relief en volume autour de la caméra,
  grottes karstiques et tunnels de lave, ciel et nuages en volume, sons, photos, vidéos, cartes,
  rapport de bug (touche ²) ; transitions en vol vers les lieux et les événements.
- **Emballage** : préréglages d'export Linux et Windows, script `tools/export.sh`.

### Qualité du code et performances (1.0)

- Revue avec les analyseurs .NET (toutes les règles) : directives inutiles, affectations et
  paramètres inutiles retirés ; aucun avertissement de compilation.
- Profil du jeu : la synthèse du son est plafonnée à deux images d'avance, par morceaux fixes
  sans allocation ; le survol de l'interface ne redemande plus la liste des panneaux au moteur ;
  les courbes ne sont redessinées qu'à l'arrivée d'un point ; le relief en volume n'alloue plus
  rien à chaque image ; les morceaux de terrain sont préparés hors du fil principal ; le calcul du
  relief est ~15 % plus rapide.
- Robustesse : recherche de cellule sûre depuis tous les threads (indice propre à chaque
  thread), attente du calcul des nuages en quittant, angles bornés avant arc sinus.

### Corrections avant la 1.0

1.  **Mer en rectangles dans les terres** : une cuvette sous le niveau de la mer, loin des côtes,
    était noyée par la mer, mais seulement dans les morceaux de terrain qui avaient une surface
    d'eau (d'où des rectangles d'eau sombre sur un désert). Loin des côtes, la mer ne monte plus.
2.  **Cyclones invisibles** : leur spirale et leur œil étaient calculés par cellule de la
    simulation (~140 km), plus grosse que l'œil : on ne voyait qu'une tache. Ils sont maintenant
    dessinés point par point.
3.  **Grottes** : galeries qui se voyaient à travers le sol, entrées parfaitement rondes, sol de
    l'entrée en roche brune aux bords déchiquetés, murs gris sous terre, paroi en cercle autour
    de l'entrée, pluie et brume dans les galeries : refait avec le relief en volume autour de la
    caméra (un seul relief continu, entrées irrégulières) ; sons de surface coupés sous terre ;
    formes reproductibles d'une partie à l'autre.
4.  **Changements de vue brutaux** : la caméra vole vers les lieux, événements, sources et
    grottes (montée, voyage, descente, le temps que le relief se charge) au lieu de s'y
    téléporter ; l'événement visé est au centre de l'écran (séismes et glaciers étaient sous la
    caméra, hors de la vue). Touche Y : arrivée en vol près de la grotte.
5.  **Fleuves et lacs qui coulaient par grand froid** : les lacs gèlent sous −2 °C, les fleuves
    par grand froid (les rapides restent ouverts), la glace se couvre de neige.
6.  **Aurores vues de l'espace** : elles s'effacent entre 8 et 25 km d'altitude.
7.  **Rubans d'eau surélevés pendant les crues** : entre un point inondé et son voisin sec, la
    surface de l'eau plongeait jusqu'au niveau de la mer et se dressait en mur vertical de la
    hauteur de la crue ; elle s'arrête désormais juste sous le sol.
8.  **Bassins en disques parfaits** (sources, geysers) : contour irrégulier, en lobes.
9.  **Eau peinte sous l'eau des sources chaudes** : le fond du bassin n'est plus peint en bleu
    (la vraie eau le teinte selon la profondeur) ; les anneaux minéraux suivent le vrai bord de
    l'eau.
11. **Pluies absurdes de l'horloge géologique** : l'estimation rapide du climat, sans saisons,
    faisait tomber la pluie en bandes étroites (la moitié des terres sous 100 mm/an, des cellules à
    20 ou 40 m/an). Elle est étalée sur les voisines (les ceintures de pluie se déplacent avec les
    saisons) et plafonnée à 12 m/an (record terrestre).
12. **Sources aussi grosses que des fleuves** : leur débit venait de la recharge d'une part de la
    cellule (~20 000 km²) ; chacune a maintenant son bassin d'alimentation (0,5 à 5 km² pour une
    source de pente, 50 à 800 km² pour une résurgence karstique).
14. **Relief en volume au-dessus de la mer** : il s'activait à 30 m au-dessus de l'eau et
    remplaçait le fond marin, 3 km plus bas (rapport de bug) ; il ne compte plus que la hauteur
    au-dessus de la roche. Le sol des blocs reprend exactement le relief détaillé et sa pente : plus
    de carrés de lumière différente.
15. **Fleuves trop hauts vus de loin** : quand le relief passe à un niveau de détail plus
    grossier, le lit étroit est lissé et les berges descendaient sous le niveau de l'eau, cachée
    jusque-là : le fleuve s'étalait en ruban au ras de la plaine. Le niveau caché suit maintenant
    le sol.
16. **Aurores coupées net à l'horizon** : bas du rideau doux, qui s'efface vers l'horizon.
17. **Fleuves tous de la même largeur, vallées en sillons** : lit selon le débit (géométrie
    hydraulique de Leopold et Maddock : largeur ≈ 3,5·Q^0,5, profondeur ≈ 0,25·Q^0,4 : grand
    fleuve de 1 000 m³/s ~110 m de large, rivière de 10 m³/s ~11 m, torrent de 1 m³/s ~3,5 m) ;
    torrents en tête de chaque cours d'eau ; vallées larges et peu profondes en plaine (plaine
    d'inondation d'une dizaine de largeurs de lit), étroites et en V en montagne ; méandres à
    l'échelle du lit ; réseau plus dense (rivières dès ~25 m³/s) ; de loin, un trait d'eau
    proportionnel à la largeur, jusqu'à l'horizon.
18. **G en orbite** : la marche s'activait à 4 000 km d'altitude (chute vers la planète) ; loin
    du sol, G fait d'abord atterrir (vol), puis marcher ; transitions plus douces de l'orbite au
    sol (altitude interpolée en échelle logarithmique).
19. **Cyclones invisibles de l'espace** : la spirale n'était ajoutée qu'aux ombres des nuages au
    sol, pas aux nuages eux-mêmes.
20. **Icebergs invisibles** : ils naissaient au centre de la cellule de mer voisine, à ~140 km de
    la côte ; ils naissent maintenant au front du glacier, sur la côte, et l'événement montre ce
    front.
21. **Flaques visibles seulement de loin** : sombres comme le sol mouillé, elles ne se voyaient
    qu'en reflet rasant ; elles reflètent maintenant le ciel de près aussi.
22. **Geysers tantôt hauts, tantôt bas** : leur cycle suivait l'horloge du climat (jusqu'à des
    mois par seconde, échantillonné au hasard) ; il suit le temps réel (accéléré au plus 3 fois).
23. **Marées trop faibles** : calculées avec la masse de la Terre, alors que la petite planète,
    à gravité terrestre, est ~10 fois moins massive ; la Lune y soulève maintenant la mer de
    ~20-25 cm de marnage (au lieu de 2 cm).
24. **Texte des détails illisible** (touche I) : retiré de l'écran (il est dans le rapport de
    bug) ; l'aide des commandes est un panneau, touches dessinées et rangées par thème.
25. **Vue à travers les grottes** : dans un tunnel de lave aplati, là où le plafond est plus bas
    que les yeux, la caméra était posée dans la roche, puis y restait coincée (et voyait à travers
    les parois) ; un passage trop bas compte maintenant comme une paroi, et une caméra coincée est
    ramenée au centre de la galerie.
26. **Relief plus loin et plus vite** : les tableaux des morceaux et leur boîte englobante sont
    préparés par les threads de calcul (le fil principal en envoie davantage par image) ; détail
    plus loin en qualité haute (9 000 morceaux en mémoire) ; vallées testées sans le bruit des
    méandres loin des cours d'eau (−15 % de temps de calcul du relief).
27. **Fentes le long du relief en volume** : un bloc dessine de −0,5 à +15,5 m de sa case ; la
    dernière demi-rangée revient à ses voisins. S'ils n'étaient pas encore construits, une fente
    laissait voir le ciel : une case ne remplace la surface qu'une fois ses voisins prêts.
28. **Icebergs disparus aussitôt vêlés** : supprimés dès que la cellule la plus proche était de
    la terre (souvent le cas près d'une côte) ; c'est maintenant le relief sous l'iceberg qui
    compte.
29. **Courbe de température en dents de scie** : un point toutes les 0,5 s tombait au hasard des
    saisons quand l'horloge va vite ; la courbe montre une moyenne glissante (jusqu'à un an).
13. **Côtes en marches d'escalier** (effet de la correction des crues) : près des côtes, la
    surface de la mer est de nouveau coupée en proportion de la pente.
10. **Contour des grottes** : parois plus irrégulières (trois échelles), couronne d'éboulis
    autour des ouvertures (calcaire clair, basalte noir des tunnels de lave).

### Corrections du 2026-11-13

1.  **Son dans l'espace** : le son suit maintenant la densité de l'air (hauteur d'échelle ~8 km) et
    s'éteint au-delà de ~60 km : silence vu de l'espace. Le grondement des volcans s'entend encore
    d'avion, pas de l'orbite ; un séisme ne se ressent qu'au sol.
2.  **L'eau des fleuves** : les fleuves sont de la vraie eau (la surface de l'eau, comme la mer et
    les lacs), qui remplit leur lit creusé ; le courant y entraîne les vaguelettes vers l'aval,
    avec des eaux blanches dans les rapides.
3.  **Biomes dès le début** : la simulation ne part pas d'une boule de feu (la planète est déjà
    refroidie, avec ses océans), mais il n'y a pas encore de vie : la vue réaliste montre donc un
    sol minéral nu selon le climat, sans forêts ni prairies. La case « Végétation imaginée »
    (Affichage) remet la végétation d'après le climat, en attendant l'étape « vie ». La couche
    « Biomes » montre les milieux que le climat permettrait.
