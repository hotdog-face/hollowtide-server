-- Combat pet export for tools/gpubox-ace/petdps (docs/proposals/summoning-curve.md): sudo -n mysql -N ace_world < pets.sql > pets.tsv
SELECT e.class_Id, e.class_Name, IFNULL(rl.value,0), pc.value, p.class_Name,
 IFNULL((SELECT value FROM weenie_properties_int WHERE object_Id=pc.value AND type=25),0),
 IFNULL((SELECT init_Level FROM weenie_properties_attribute WHERE object_Id=pc.value AND type=1),0),
 IFNULL((SELECT init_Level FROM weenie_properties_attribute WHERE object_Id=pc.value AND type=4),0),
 IFNULL((SELECT value FROM weenie_properties_float WHERE object_Id=pc.value AND type=34),-1),
 IFNULL((SELECT value FROM weenie_properties_d_i_d WHERE object_Id=pc.value AND type=2),0),
 IFNULL((SELECT value FROM weenie_properties_d_i_d WHERE object_Id=pc.value AND type=4),0),
 IFNULL((SELECT GROUP_CONCAT(CONCAT(bp.key,':',bp.d_Type,':',bp.d_Val,':',bp.d_Var) SEPARATOR ' ') FROM weenie_properties_body_part bp WHERE bp.object_Id=pc.value AND bp.d_Val>0),'')
FROM weenie_properties_int pc JOIN weenie e ON e.class_Id=pc.object_Id AND pc.type=266
LEFT JOIN weenie_properties_int rl ON rl.object_Id=e.class_Id AND rl.type=369
JOIN weenie p ON p.class_Id=pc.value
WHERE (SELECT value FROM weenie_properties_int WHERE object_Id=pc.value AND type=25) >= 15
  AND EXISTS (SELECT 1 FROM weenie_properties_body_part bp WHERE bp.object_Id=pc.value AND bp.d_Val>0)
ORDER BY 6, e.class_Name;
