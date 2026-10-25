using GameManager.OnlineBase;
using GameManager.OnlineDefault;
using UnityEngine;

namespace WheelchairLocalCoop;

internal static class WeaponHud
{
    private static GUIStyle crosshairStyle;
    private static GUIStyle inventoryStyle;
    private static GUIStyle hintStyle;
    private static GUIStyle ammoStyle;
    private static readonly WeaponsCollectable[] order={WeaponsCollectable.Punch,WeaponsCollectable.Rpg,WeaponsCollectable.StickyBomb};
    internal static void Draw(int seat,OnlineBasePlayerRef actor,bool showInventory=true)
    {
        var manager=actor.GetComponentInChildren<OnDefaultWeaponsManager>(true);
        if(!manager || !manager.initialized) return;
        float w=Screen.width*0.5f,h=Screen.height*0.5f;
        float x=(seat%2)*w,y=(seat/2)*h;
        int rpg=0,c4=0;
        foreach(var weapon in manager.GetWeaponsList())
        {
            if(weapon.WeaponType==WeaponsCollectable.Rpg) rpg=weapon.CurrentAmmo;
            if(weapon.WeaponType==WeaponsCollectable.StickyBomb) c4=weapon.CurrentAmmo;
        }
        var selected=manager.CurrentWeapon?manager.CurrentWeapon.WeaponType:WeaponsCollectable.Punch;
        if(showInventory)
        {
            if(inventoryStyle==null)
            {
                inventoryStyle=new GUIStyle(GUI.skin.label){fontSize=18,fontStyle=FontStyle.Bold,alignment=TextAnchor.UpperCenter};
                inventoryStyle.normal.textColor=Color.white;
                hintStyle=new GUIStyle(GUI.skin.label){fontSize=16,alignment=TextAnchor.MiddleRight};
                hintStyle.normal.textColor=Color.white;
                ammoStyle=new GUIStyle(GUI.skin.label){fontSize=22,fontStyle=FontStyle.Bold,alignment=TextAnchor.LowerRight};
                ammoStyle.normal.textColor=Color.white;
            }
            float width=Mathf.Min(w-28,324),left=x+w-width-14,top=y+h-106;
            GUI.Label(new Rect(left,top,width,24),"P"+(seat+1)+"  |  D-pad Left / Right",hintStyle);
            float cardWidth=(width-12)/3;
            for(int j=0;j<order.Length;j++)
            {
                var type=order[j];bool active=type==selected;
                var card=new Rect(left+j*(cardWidth+6),top+27,cardWidth,64);
                var oldColor=GUI.color;
                GUI.color=active?new Color(0.3f,0.85f,1f,1f):Color.white;
                GUI.Box(card,"");GUI.color=oldColor;
                string name=type==WeaponsCollectable.Punch?"PUSH":type==WeaponsCollectable.Rpg?"RPG":"C4";
                GUI.Label(new Rect(card.x+2,card.y+2,cardWidth-4,25),active?"[ "+name+" ]":name,inventoryStyle);
                string ammo=type==WeaponsCollectable.Punch?"--":(type==WeaponsCollectable.Rpg?rpg:c4).ToString();
                GUI.Label(new Rect(card.x+4,card.y+33,cardWidth-12,28),ammo,ammoStyle);
            }
        }
        if(selected!=WeaponsCollectable.Punch && manager.CameraController && manager.CameraController.IsAiming)
        {
            if(crosshairStyle==null) crosshairStyle=new GUIStyle(GUI.skin.label){alignment=TextAnchor.MiddleCenter,fontSize=28,fontStyle=FontStyle.Bold};
            var reticle=new Rect(x+w*0.5f-20,y+h*0.5f-20,40,40);
            crosshairStyle.normal.textColor=Color.red;
            GUI.Label(reticle,"+",crosshairStyle);
        }
    }

}
