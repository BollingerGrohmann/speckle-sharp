﻿using Newtonsoft.Json;
using Objects.Geometry;
using Speckle.Core.Kits;
using Speckle.Core.Models;
using System.Collections.Generic;

namespace Objects.BuiltElements.Revit
{
  public class FamilyInstance : Base
  {
    public Point basePoint { get; set; }

    public string family { get; set; }

    public string type { get; set; }
    public Level level { get; set; }

    public double rotation { get; set; }

    public bool facingFlipped { get; set; }

    public bool handFlipped { get; set; }

    public Dictionary<string, object> parameters { get; set; }

    public Dictionary<string, object> typeParameters { get; set; }

    public string elementId { get; set; }

    public List<Base> elements { get; set; }

    public FamilyInstance()
    {

    }

    [SchemaInfo("FamilyInstance", "Creates a Revit family instance")]
    public FamilyInstance(Point basePoint, string family, string type, Level level,
      double rotation = 0, bool facingFlipped = false, bool handFlipped = false,
      Dictionary<string, object> parameters = null)
    {
      this.basePoint = basePoint;
      this.family = family;
      this.type = type;
      this.level = level;
      this.rotation = rotation;
      this.facingFlipped = facingFlipped;
      this.handFlipped = handFlipped;
      this.parameters = parameters;
    }
  }
}