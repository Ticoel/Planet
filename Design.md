# Planet — décisions de conception

Mise à jour : 2026-11-07

## Vision
Planète **entièrement procédurale** : tectonique, relief, climat, cycle de l'eau et biomes émergent de la simulation et tournent seuls. Pas de données de la vraie Terre.

## Choix techniques
- Godot 4.7.2 .NET, C# pour la logique CPU et compute shaders GLSL (Forward+, Vulkan) pour la physique massivement parallèle
- Grille : icosphère subdivisée (cellules quasi uniformes, pas de singularité aux pôles), résolution réglable
- Séparation stricte simulation (données + physique) / rendu (globe + couches de visualisation)

## Deux horloges imbriquées
- **Géologique** (pas de ~0,1 à 1 Ma) : plaques, croûte, isostasie, érosion
- **Climatique** (pas d'une heure à un jour) : ensoleillement, températures, vents, eau, sur un relief figé, jusqu'à l'équilibre
- Couplage : le climat fournit les précipitations → érosion ; le relief modifié → nouveau climat

## Tectonique (réf. Cortial et al. 2019, *Procedural Tectonic Planets*)
- Chaque cellule appartient à une plaque ; chaque plaque tourne autour d'un pôle d'Euler
- Croûte océanique (fine, dense, jeune) / continentale (épaisse, légère, ancienne) ; altitude par isostasie
- Frontières : convergence (subduction, fosses, arcs volcaniques, collision → chaînes), divergence (dorsales, rifts), transformante
- Fracturation / fusion de plaques occasionnelles ; départ depuis un supercontinent ou un océan global

## Étapes
1. Globe en icosphère + structure de données des cellules
2. Tectonique : plaques, croûte, relief
3. Ensoleillement et saisons
4. Températures, inertie océan/continent
5. Vents (Coriolis, cellules de circulation)
6. Cycle de l'eau
7. Rivières, glaciers, érosion (couplage tectonique)
8. Biomes et rétroactions
