package com.spindletrack.android.data
import android.content.Context
import androidx.room.*
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase
@Entity(tableName="snapshots", primaryKeys=["account","key"])
data class Snapshot(val account:String,val key:String,val value:String)
@Entity(tableName="outbox")
/** [version] is the entry's as this device last had it from the server: the change is sent as If-Match, so it fails instead of overwriting a change made elsewhere since. Null for new entries and changes queued before versions. */
data class PendingChange(@PrimaryKey(autoGenerate=true) val id:Long=0,val account:String,val workspaceId:String,val entryId:String,val method:String,val path:String,val body:String?,val optimistic:String?,val error:String?=null,val version:String?=null)
@Dao interface StoreDao {
 @Query("SELECT * FROM snapshots WHERE account=:account AND `key`=:key") suspend fun get(account:String,key:String):Snapshot?
 @Insert(onConflict=OnConflictStrategy.REPLACE) suspend fun put(snapshot:Snapshot)
 @Insert suspend fun add(change:PendingChange):Long
 @Query("SELECT * FROM outbox WHERE account=:account ORDER BY id") suspend fun pending(account:String):List<PendingChange>
 @Query("DELETE FROM outbox WHERE id=:id") suspend fun remove(id:Long)
 @Query("UPDATE outbox SET error=:error WHERE id=:id") suspend fun fail(id:Long,error:String?)
 @Query("UPDATE outbox SET version=:version WHERE id=:id") suspend fun setVersion(id:Long,version:String?)
 @Query("DELETE FROM outbox WHERE account=:account") suspend fun clearPending(account:String)
 @Query("DELETE FROM snapshots WHERE account=:account") suspend fun clearSnapshots(account:String)
}
@Database(entities=[Snapshot::class,PendingChange::class],version=2,exportSchema=false)
abstract class SpindleDatabase:RoomDatabase(){
 abstract fun dao():StoreDao
 companion object {
 /** Changes queued before versions keep going through as they are. */
 val AddVersion=object:Migration(1,2){override fun migrate(db:SupportSQLiteDatabase){db.execSQL("ALTER TABLE outbox ADD COLUMN version TEXT")}}
 @Volatile private var instance:SpindleDatabase?=null
 fun get(context:Context):SpindleDatabase=instance?:synchronized(this){instance?:Room.databaseBuilder(context.applicationContext,SpindleDatabase::class.java,"spindle.db").addMigrations(AddVersion).build().also{instance=it}}
 }
}
