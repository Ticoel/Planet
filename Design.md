# Planet — décisions de conception

Mise à jour : 2026-11-09 (étapes 1 à 9 faites)

## Vision
Planète **entièrement procédurale** : tectonique, relief, climat, cycle de l'eau et biomes émergent de la simulation et tournent seuls. Pas de données de la vraie Terre. Exploration de l'orbite jusqu'au sol, à l'échelle réelle.

## Choix techniques
- Godot 4.7.2 .NET ; C# pour la logique CPU, compute shaders GLSL pour le climat (vérifiés contre la référence CPU au démarrage, repli automatique sur le CPU).
- **Deux grilles qui cohabitent** :
  - simulation sur une **icosphère** (163 842 cellules hexagonales de ~60 km à n = 7) : voisines à égale distance, pas de singularité aux pôles ;
  - affichage sur une **sphère cubique** découpée en quadtree (morceaux carrés de ~2 500 km à 38 m) : niveau de détail selon la distance.
  - Lien : le relief détaillé interpole les altitudes des cellules et y ajoute les détails ; les couches de couleur sont lues dans les cellules (carte « numéro de cellule » par face du cube) et fondues entre voisines.
- Séparation stricte simulation (`Simulation/`, sans Godot hors types de base) / rendu (`Rendering/`, `Main.cs`).
- Conventions : identifiants, fichiers et console en anglais ; commentaires et interface en français.

## Deux horloges (touche C)
- **Géologique** (pas de 0,2 à 1 Ma) : plaques, croûte, isostasie, érosion. Le relief affiché suit (nouveau relief préparé en arrière-plan puis basculé d'un coup).
- **Climatique** (du temps réel à 3 mois/s) : ensoleillement, températures, vents, eau, sur un relief figé.
- Couplage : un climat moyen estimé tous les 25 Ma fournit les précipitations → érosion et vallées.
- Avance rapide jusqu'à 4,54 Ga (touche T) ; vitesses jusqu'à 500 Ma/s (vitesse réelle limitée par le calcul).

## Tectonique (réf. Cortial et al. 2019, *Procedural Tectonic Planets*)
- Chaque cellule appartient à une plaque ; chaque plaque tourne autour d'un pôle d'Euler.
- Croûte océanique / continentale ; altitude par isostasie ; subduction, dorsales, collisions, fracturations et fusions.
- Vérifié stable jusqu'à 4,5 Ga (15 plaques, 27 à 30 % de terres, sommets ~7 km).

## Climat (étapes 3 à 8)
- Bilan d'énergie (A = 188, B = 2,09, D = 0,35, capacité de l'air 2,5e6), vents diagnostiques, cycle de l'eau, réseau hydrographique (priority-flood), biomes de Köppen.

## Terrain (étape 9)
- **CDLOD** : niveaux 0–18, 32×32 quads par morceau, jupes, élimination hors champ et sous l'horizon, cache LRU de 3 000 morceaux (les parents traversés comptent comme utilisés). Au bout du morphing, chaque morceau coïncide exactement avec son parent (cibles calculées avec les longueurs d'onde du parent).
- Calcul des morceaux sur des threads, en double précision ; rendu relatif à la caméra ; near = far / 10⁶.
- **Relief** (`TerrainHeightField.Sample`) :
  - grandes formes : plans de Shepard + poids de Wendland C2 ;
  - détail fractal (plaines 80 m, collines 650 m, montagnes « ridged » 2 300 m aux arêtes arrondies sur ~30 m, fonds 300 m) ; érosion par les dérivées du bruit ;
  - plis le long des chaînes allongées seulement (pente de direction cohérente), crêtes ondulées et morcelées ;
  - vallées des fleuves : tracés courbes (spline de Catmull-Rom par les cellules + grandes boucles + méandres), largeur et profondeur selon le débit ;
  - lacs : niveau de l'eau qui s'abaisse vers le bord de la zone du lac (rive continue, relief intact) ; falaises côtières par endroits.
- **Rendu** :
  - eau = seconde instance du même maillage, transparente selon la profondeur, vaguelettes ; touche U pour la masquer ;
  - textures procédurales dans le shader (bruit périodique recalé sur la caméra) : micro-relief jusqu'à ~2 m, variations de couleur, strates, éboulis ;
  - neige selon le climat simulé (fondue entre cellules) ; ciel avec couchers de soleil ; brume ;
  - couche altitude calculée point par point ; autres couches fondues entre cellules (~40 km) ; vue réaliste qui ne mélange que des cellules du même milieu (terre ou mer).
- Caméra : vol à l'horizontale qui suit la courbure, marche, zoom à la molette, glisser à la souris ; direction de marche transportée (pas de singularité aux pôles).

## Tests (`tests/Planet.Tests`)
- `dotnet run -c Release --project tests/Planet.Tests` (~45 s), `-- --quick`, `-- Terrain`. 83 tests.

## Suite possible
- Impacts météoritiques occasionnels, vue sous-marine, végétation, sauvegarde/chargement, niveau de la mer selon le volume d'eau, courants océaniques.
