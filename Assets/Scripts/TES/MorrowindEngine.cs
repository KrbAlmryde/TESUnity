﻿using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace TESUnity
{
	using ESM;
	
	public class MorrowindEngine
	{
		public MorrowindEngine(MorrowindDataReader dataReader)
		{
			this.dataReader = dataReader;

			waterObj = GameObject.Instantiate(TESUnity.instance.waterPrefab);
		}

		public GameObject InstantiateNIF(string filePath)
		{
			NIF.NiFile file = dataReader.LoadNIF(filePath);

			if(prefabObj == null)
			{
				prefabObj = new GameObject("Prefabs");
				prefabObj.SetActive(false);
			}

			GameObject prefab;

			if(!loadedNIFObjects.TryGetValue(filePath, out prefab))
			{
				var objBuilder = new NIFObjectBuilder(file, dataReader);
				prefab = objBuilder.BuildObject();

				prefab.transform.parent = prefabObj.transform;

				loadedNIFObjects[filePath] = prefab;
			}

			return GameObject.Instantiate(prefab);
		}
		public GameObject InstantiateCell(CELLRecord CELL)
		{
			Debug.Assert(CELL != null);

			if(!CELL.isInterior)
			{
				var cellIndices = new Vector2i(CELL.DATA.gridX, CELL.DATA.gridY);
				var LAND = dataReader.FindLANDRecord(cellIndices.x, cellIndices.y);

				if(LAND != null)
				{
					var cellObj = new GameObject("cell " + cellIndices.ToString());
					cellObj.tag = "Cell";

					var landObj = InstantiateLAND(LAND);

					if(landObj != null)
					{
						landObj.transform.parent = cellObj.transform;
					}

					InstantiateCellObjects(CELL, cellObj);

					return cellObj;
				}
				else
				{
					return null;
				}
			}
			else
			{
				GameObject cellObj = new GameObject(CELL.NAME.value);
				cellObj.tag = "Cell";

				InstantiateCellObjects(CELL, cellObj);

				return cellObj;
			}
		}
		public GameObject InstantiateExteriorCell(int x, int y)
		{
			var CELL = dataReader.FindExteriorCellRecord(x, y);

			if(CELL != null)
			{
				return InstantiateCell(CELL);
			}
			else
			{
				return null;
			}
		}
		public GameObject InstantiateInteriorCell(string cellName)
		{
			var CELL = dataReader.FindInteriorCellRecord(cellName);

			if(CELL != null)
			{
				return InstantiateCell(CELL);
			}
			else
			{
				return null;
			}
		}
		public GameObject InstantiateLAND(LANDRecord LAND)
		{
			// Don't create anything if the LAND doesn't have height data.
			if(LAND.VHGT == null)
			{
				return null;
			}

			int LAND_SIDE_LENGTH_IN_SAMPLES = 65;
			var heights = new float[LAND_SIDE_LENGTH_IN_SAMPLES, LAND_SIDE_LENGTH_IN_SAMPLES];

			// Read in the heights in Morrowind units.
			const int VHGTIncrementToMWUnits = 8;
			float rowOffset = LAND.VHGT.referenceHeight;

			for(int y = 0; y < LAND_SIDE_LENGTH_IN_SAMPLES; y++)
			{
				rowOffset += LAND.VHGT.heightOffsets[y * LAND_SIDE_LENGTH_IN_SAMPLES];
				heights[y, 0] = VHGTIncrementToMWUnits * rowOffset;

				float colOffset = rowOffset;

				for(int x = 1; x < LAND_SIDE_LENGTH_IN_SAMPLES; x++)
				{
					colOffset += LAND.VHGT.heightOffsets[(y * LAND_SIDE_LENGTH_IN_SAMPLES) + x];
					heights[y, x] = VHGTIncrementToMWUnits * colOffset;
				}
			}

			// Change the heights to percentages.
			float minHeight, maxHeight;
			Utils.GetExtrema(heights, out minHeight, out maxHeight);

			for(int y = 0; y < LAND_SIDE_LENGTH_IN_SAMPLES; y++)
			{
				for(int x = 0; x < LAND_SIDE_LENGTH_IN_SAMPLES; x++)
				{
					heights[y, x] = Utils.ChangeRange(heights[y, x], minHeight, maxHeight, 0, 1);
				}
			}

			// Texture the terrain.
			SplatPrototype[] splatPrototypes = null;
			float[,,] alphaMap = null;

			if(LAND.VTEX != null)
			{
				// Create splat prototypes.
				var splatPrototypeList = new List<SplatPrototype>();
				var texInd2splatInd = new Dictionary<ushort, int>();

				for(int i = 0; i < LAND.VTEX.textureIndices.Length; i++)
				{
					short textureIndex = (short)((short)LAND.VTEX.textureIndices[i] - 1);

					if(textureIndex < 0)
					{
						continue;
					}

					if(!texInd2splatInd.ContainsKey((ushort)textureIndex))
					{
						// Load terrain texture.
						var LTEX = dataReader.FindLTEXRecord(textureIndex);
						var textureFileName = LTEX.DATA.value;
						var textureName = Path.GetFileNameWithoutExtension(textureFileName);
						var texture = dataReader.LoadTexture(textureName);

						// Create the splat prototype.
						var splat = new SplatPrototype();
						splat.texture = texture;
						splat.smoothness = 0;
						splat.metallic = 0;

						// Update collections.
						var splatIndex = splatPrototypeList.Count;
						splatPrototypeList.Add(splat);
						texInd2splatInd.Add((ushort)textureIndex, splatIndex);
					}
				}

				splatPrototypes = splatPrototypeList.ToArray();

				// Create the alpha map.
				int VTEX_ROWS = 16;
				int VTEX_COLUMNS = VTEX_ROWS;
				alphaMap = new float[VTEX_ROWS, VTEX_COLUMNS, splatPrototypes.Length];

				for(int y = 0; y < VTEX_ROWS; y++)
				{
					var yMajor = y / 4;
					var yMinor = y - (yMajor * 4);

					for(int x = 0; x < VTEX_COLUMNS; x++)
					{
						var xMajor = x / 4;
						var xMinor = x - (xMajor * 4);

						var texIndex = (short)((short)LAND.VTEX.textureIndices[(yMajor * 64) + (xMajor * 16) + (yMinor * 4) + xMinor] - 1);

						if(texIndex >= 0)
						{
							var splatIndex = texInd2splatInd[(ushort)texIndex];

							alphaMap[y, x, splatIndex] = 1;
						}
						else
						{
							alphaMap[y, x, 0] = 1;
						}
					}
				}
			}

			// Create the terrain.
			var heightRange = maxHeight - minHeight;
			var terrainPosition = new Vector3(Convert.exteriorCellSideLengthInMeters * LAND.gridCoords.x, minHeight / Convert.meterInMWUnits, Convert.exteriorCellSideLengthInMeters * LAND.gridCoords.y);

			var heightSampleDistance = Convert.exteriorCellSideLengthInMeters / (LAND_SIDE_LENGTH_IN_SAMPLES - 1);
			var terrain = GameObjectUtils.CreateTerrain(heights, heightRange / Convert.meterInMWUnits, heightSampleDistance, splatPrototypes, alphaMap, terrainPosition);
			terrain.GetComponent<Terrain>().materialType = Terrain.MaterialType.BuiltInLegacyDiffuse;
			return terrain;
		}

		public void Update()
		{
			if(!isInteriorCell)
			{
				UpdateExteriorCells();

				if(Input.GetKeyDown(KeyCode.P))
				{
					Debug.Log(GetExteriorCellIndices(Camera.main.transform.position));
				}
			}
		}
		public void TryOpenDoor()
		{
			RaycastHit hitInfo;
			var ray = new Ray(Camera.main.transform.position, Camera.main.transform.forward);

			if(Physics.Raycast(ray, out hitInfo, 2))
			{
				// Find the door object.
				GameObject doorObj = GameObjectUtils.FindObjectWithTagUpHeirarchy(hitInfo.collider.gameObject, "Door");

				if(doorObj != null)
				{
					var doorComponent = doorObj.GetComponent<DoorComponent>();

					if(doorComponent.leadsToAnotherCell)
					{
						DestroyAllCells();

						ESM.CELLRecord CELL;

						if((doorComponent.doorExitName != null) && (doorComponent.doorExitName != ""))
						{
							CELL = dataReader.FindInteriorCellRecord(doorComponent.doorExitName);
							cellObjects[Vector2i.zero] = InstantiateCell(CELL);

							if(CELL.WHGT != null)
							{
								waterObj.transform.position = new Vector3(0, CELL.WHGT.value, 0);
								waterObj.SetActive(true);
							}
							else
							{
								waterObj.SetActive(false);
							}
						}
						else
						{
							var cellIndices = GetExteriorCellIndices(doorComponent.doorExitPos);
							CELL = dataReader.FindExteriorCellRecord(cellIndices.x, cellIndices.y);

							waterObj.transform.position = Vector3.zero;
							waterObj.SetActive(true);
						}

						Camera.main.transform.position = doorComponent.doorExitPos;
						Camera.main.transform.rotation = doorComponent.doorExitOrientation;

						isInteriorCell = CELL.isInterior;
					}
				}
			}
		}

		private MorrowindDataReader dataReader;

		private Dictionary<string, GameObject> loadedNIFObjects = new Dictionary<string, GameObject>();
		private GameObject prefabObj;

		private Dictionary<Vector2i, GameObject> cellObjects = new Dictionary<Vector2i, GameObject>();
		private int cellRadius = 1;
		private bool isInteriorCell;

		private GameObject waterObj;

		private void InstantiateCellObjects(CELLRecord CELL, GameObject parent)
		{
			foreach(var refObjGroup in CELL.refObjDataGroups)
			{
				Record objRecord;

				if(dataReader.MorrowindESMFile.objectsByIDString.TryGetValue(refObjGroup.NAME.value, out objRecord))
				{
					string modelFileName = null;

					if(objRecord is STATRecord)
					{
						var record = (STATRecord)objRecord;
						modelFileName = record.MODL.value;
					}
					else if(objRecord is DOORRecord)
					{
						var record = (DOORRecord)objRecord;
						modelFileName = record.MODL.value;
					}
					else if(objRecord is MISCRecord)
					{
						var record = (MISCRecord)objRecord;
						modelFileName = record.MODL.value;
					}
					else if(objRecord is WEAPRecord)
					{
						var record = (WEAPRecord)objRecord;
						modelFileName = record.MODL.value;
					}
					else if(objRecord is CONTRecord)
					{
						var record = (CONTRecord)objRecord;
						modelFileName = record.MODL.value;
					}
					else if(objRecord is LIGHRecord)
					{
						var record = (LIGHRecord)objRecord;
						modelFileName = record.MODL.value;
					}
					else if(objRecord is ARMORecord)
					{
						var record = (ARMORecord)objRecord;
						modelFileName = record.MODL.value;
					}
					else if(objRecord is CLOTRecord)
					{
						var record = (CLOTRecord)objRecord;
						modelFileName = record.MODL.value;
					}
					else if(objRecord is REPARecord)
					{
						var record = (REPARecord)objRecord;
						modelFileName = record.MODL.value;
					}
					else if(objRecord is ACTIRecord)
					{
						var record = (ACTIRecord)objRecord;
						modelFileName = record.MODL.value;
					}
					else if(objRecord is APPARecord)
					{
						var record = (APPARecord)objRecord;
						modelFileName = record.MODL.value;
					}
					else if(objRecord is LOCKRecord)
					{
						var record = (LOCKRecord)objRecord;
						modelFileName = record.MODL.value;
					}
					else if(objRecord is PROBRecord)
					{
						var record = (PROBRecord)objRecord;
						modelFileName = record.MODL.value;
					}
					else if(objRecord is INGRRecord)
					{
						var record = (INGRRecord)objRecord;
						modelFileName = record.MODL.value;
					}
					else if(objRecord is BOOKRecord)
					{
						var record = (BOOKRecord)objRecord;
						modelFileName = record.MODL.value;
					}
					else if(objRecord is ALCHRecord)
					{
						var record = (ALCHRecord)objRecord;
						modelFileName = record.MODL.value;
					}

					if((modelFileName != null) && (modelFileName != ""))
					{
						var modelFilePath = "meshes\\" + modelFileName;

						var obj = InstantiateNIF(modelFilePath);

						if(refObjGroup.XSCL != null)
						{
							obj.transform.localScale = Vector3.one * refObjGroup.XSCL.value;
						}

						obj.transform.position = Convert.NifPointToUnityPoint(refObjGroup.DATA.position);
						obj.transform.rotation = Convert.NifEulerAnglesToUnityQuaternion(refObjGroup.DATA.eulerAngles);

						obj.transform.parent = parent.transform;

						if(objRecord is DOORRecord)
						{
							obj.tag = "Door";

							var DOOR = (DOORRecord)objRecord;
							var doorComponent = obj.AddComponent<DoorComponent>();

							if(DOOR.FNAM != null)
							{
								doorComponent.doorName = DOOR.FNAM.value;
							}

							if((refObjGroup.DNAM != null) || (refObjGroup.DODT != null))
							{
								doorComponent.leadsToAnotherCell = true;

								if(refObjGroup.DNAM != null)
								{
									doorComponent.doorExitName = refObjGroup.DNAM.value;
								}

								if(refObjGroup.DODT != null)
								{
									doorComponent.doorExitPos = Convert.NifPointToUnityPoint(refObjGroup.DODT.position);
									doorComponent.doorExitOrientation = Convert.NifEulerAnglesToUnityQuaternion(refObjGroup.DODT.eulerAngles);
								}
							}
							else
							{
								doorComponent.leadsToAnotherCell = false;
							}
						}
					}
				}
				/*else
				{
					Debug.Log("Unknown Object: " + refObjGroup.NAME.value);
				}*/
			}
		}

		private Vector2i GetExteriorCellIndices(Vector3 point)
		{
			return new Vector2i(Mathf.FloorToInt(point.x / Convert.exteriorCellSideLengthInMeters), Mathf.FloorToInt(point.z / Convert.exteriorCellSideLengthInMeters));
		}
		private void UpdateExteriorCells()
		{
			var cameraCellIndices = GetExteriorCellIndices(Camera.main.transform.position);
			var minCellX = cameraCellIndices.x - cellRadius;
			var maxCellX = cameraCellIndices.x + cellRadius;
			var minCellY = cameraCellIndices.y - cellRadius;
			var maxCellY = cameraCellIndices.y + cellRadius;

			// Destroy out of range cells.
			var outOfRangeCellIndices = new List<Vector2i>();

			foreach(var KVPair in cellObjects)
			{
				if((KVPair.Key.x < minCellX) || (KVPair.Key.x > maxCellX) || (KVPair.Key.y < minCellY) || (KVPair.Key.y > maxCellY))
				{
					outOfRangeCellIndices.Add(KVPair.Key);
				}
			}

			foreach(var cellIndices in outOfRangeCellIndices)
			{
				DestroyExteriorCell(cellIndices);
			}

			// Create new cells.
			for(int x = minCellX; x <= maxCellX; x++)
			{
				for(int y = minCellY; y <= maxCellY; y++)
				{
					var cellIndices = new Vector2i(x, y);

					if(!cellObjects.ContainsKey(cellIndices))
					{
						CreateExteriorCell(cellIndices);
					}
				}
			}
		}
		private GameObject CreateExteriorCell(Vector2i indices)
		{
			var cellObj = InstantiateExteriorCell(indices.x, indices.y);
			cellObjects[indices] = cellObj;

			return cellObj;
		}
		private void DestroyExteriorCell(Vector2i indices)
		{
			GameObject cellObj;

			if(cellObjects.TryGetValue(indices, out cellObj))
			{
				cellObjects.Remove(indices);
				GameObject.Destroy(cellObj);
			}
			else
			{
				Debug.LogError("Tried to destroy a cell that isn't created.");
			}
		}
		private GameObject CreateInteriorCell(string cellName)
		{
			var cellObj = InstantiateInteriorCell(cellName);
			cellObjects[Vector2i.zero] = cellObj;

			return cellObj;
		}
		private void DestroyAllCells()
		{
			foreach(var keyValuePair in cellObjects)
			{
				GameObject.Destroy(keyValuePair.Value);
			}

			cellObjects.Clear();
		}
	}
}